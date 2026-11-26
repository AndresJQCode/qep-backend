using BuildingBlocks.Application;
using Microsoft.Extensions.Logging.Abstractions;
using Modules.Ai.Application;
using Modules.Ai.Domain;

namespace Modules.Ai.UnitTests;

/// <summary>
/// Spec 2026-10-29 §6.2, §6.3, §6.6 y §6.8; plan 2026-11-12 C-19 y Review Focus 4 y 6: el procesador de entrantes y el commit corto del worker,
/// con dobles en memoria de los puertos. El modelo no se llama: el commit corto se prueba con la respuesta ya guardada en el trabajo (§6.3
/// paso 4), que es el mismo camino que sigue una respuesta recién calculada.
/// </summary>
public sealed class AgentSessionProcessorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    // Internos: AgentBlockSessionProcessorTests reusa los dobles, que los fijan.
    internal static readonly Guid Tenant = Guid.CreateVersion7();
    internal static readonly Guid Connection = Guid.CreateVersion7();
    internal static readonly Guid Conversation = Guid.CreateVersion7();
    private static readonly Guid By = Guid.CreateVersion7();

    private sealed class World
    {
        public FakeUnitOfWork UnitOfWork { get; } = new();

        public FakeSessions Sessions { get; } = new();

        public FakeJobs Jobs { get; } = new();

        public FakeInbox Inbox { get; } = new();

        public FakeConversations Conversations { get; } = new();

        public FakeCredentials Credentials { get; } = new();

        public FakeOutbound Outbound { get; } = new();

        public FakeModules Modules { get; } = new();

        public FakeHours Hours { get; } = new();

        public FixedClock Clock { get; } = new(Now);

        public FakeAudit Audit { get; } = new();

        /// <summary>El modelo de mentira: cada respuesta encolada se entrega una vez; una llamada de más falla la prueba.</summary>
        public ScriptedModel Model { get; } = new();

        public FakeDrafts Drafts { get; } = new();

        public AiInboxProcessor Inbound() => new(
            UnitOfWork, Sessions, Jobs, Inbox, Conversations, Hours, Modules, Credentials, Outbound, AiSessionTimings.Default, Clock,
            NullLogger<AiInboxProcessor>.Instance);

        public AgentSessionJobProcessor Worker()
        {
            var limits = new AiRunLimits(16_000, TimeSpan.FromSeconds(120), 6, 8, 30, null);
            var runner = new AgentTurnRunner(
                new SingleResolver(Model), [new CreateQuotationDraftTool(Drafts, new FakeAdvisors())], new NoLedger(), Credentials, limits, Clock,
                NullLogger<AgentTurnRunner>.Instance);
            return new(
                UnitOfWork, Sessions, Jobs, Conversations, Outbound, Modules, Credentials, new EmptyCatalog(), new NoQuickReplies(),
                new UtcTenantClock(), new AgentPromptBuilder(limits), runner, Clock, NullLogger<AgentSessionJobProcessor>.Instance, Audit);
        }
    }

    private static readonly Guid Customer = Guid.CreateVersion7();
    private static readonly Guid Advisor = Guid.CreateVersion7();

    /// <summary>Un agente con <c>create_quotation_draft</c> activo, su asesora y su cuenta de cobro.</summary>
    private static Agent QuotingAgent()
    {
        var config = AgentConfig.Default(new AgentModelRef("anthropic", "claude-opus-5-5")) with
        {
            Instructions = "Cotiza.",
            Tools = new AgentTools(Catalog: false, OrderStatus: false, QuotationDraft: true),
            QuotationDraft = new AgentQuotationDraft(Advisor, new AgentBillingAccount(Guid.CreateVersion7(), "Bancolombia", "123-456", "COP")),
        };
        var agent = Agent.Create(Tenant, "Cotizador", config, By, Now);
        agent.SetConnections([Connection], Now);
        agent.Publish([], By, Now);
        return agent;
    }

    private static Agent PublishedAgent(
        string provider = "anthropic", string? greeting = null, int maxReplies = 20, string trigger = "NewConversation", Guid? destinationMemberId = null)
    {
        var config = AgentConfig.Default(new AgentModelRef(provider, provider == "anthropic" ? "claude-opus-5-5" : "gpt-6-luna")) with
        {
            Instructions = "Responde dudas.",
            Greeting = greeting,
            Trigger = trigger,
        };
        config = config with { Handoff = config.Handoff with { MaxRepliesWithoutHuman = maxReplies } };
        if (destinationMemberId is { } member)
        {
            config = config with { Handoff = config.Handoff with { Destination = AgentHandoff.ToMember, MemberId = member } };
        }

        var agent = Agent.Create(Tenant, "Asesor virtual", config, By, Now);
        agent.SetConnections([Connection], Now);
        agent.Publish([], By, Now);
        return agent;
    }

    private static AiInboxItem Inbound() =>
        new(Guid.CreateVersion7(), Tenant, Connection, Conversation, Now, 1, ReleaseRun: false);

    private static AiConversationState State(
        string automation = "None", string? bot = null, Guid? agentId = null, bool windowOpen = true, Guid? customerId = null) =>
        new("Open", null, automation, bot, agentId, Connection, "WhatsApp", customerId, null, false, windowOpen, TimeSpan.FromHours(20), 4096);

    [Fact]
    public async Task ANewConversationStartsASessionWithItsGreetingAndAJob()
    {
        var world = new World();
        var agent = PublishedAgent(greeting: "¡Hola! Soy el asesor virtual.");
        world.Sessions.Serving = agent;
        world.Conversations.State = State();
        world.Credentials.Available.Add("anthropic");

        var outcome = await world.Inbound().ProcessAsync(Inbound(), TestContext.Current.CancellationToken);

        Assert.Equal(AiInboxOutcomes.Started, outcome);
        Assert.Equal(agent.Id, world.Conversations.StartedAgent);
        var session = Assert.Single(world.Sessions.All);
        Assert.Equal(agent.PublishedVersionId, session.AgentVersionId);
        Assert.Equal([AgentTurnRoleKind.Greeting, AgentTurnRoleKind.Customer], world.Sessions.Turns.Select(turn => turn.Role));
        Assert.Equal("¡Hola! Soy el asesor virtual.", Assert.Single(world.Outbound.Sends).Text);
        var job = Assert.Single(world.Jobs.Added);
        Assert.Equal((session.Id, 2, Now + AiSessionTimings.Default.Debounce), (job.SessionId!.Value, job.UpToSeq, job.NextAttemptAt));
    }

    // C-19 y Review Focus 4: la cuenta que importa es la del proveedor del agente; la del otro no lo habilita.
    [Fact]
    public async Task WithoutTheAgentsProviderAccountNoSessionOpensEvenIfTheOtherProviderIsConnected()
    {
        var world = new World();
        world.Sessions.Serving = PublishedAgent(provider: "anthropic");
        world.Conversations.State = State();
        world.Credentials.Available.Add("openai");

        var outcome = await world.Inbound().ProcessAsync(Inbound(), TestContext.Current.CancellationToken);

        Assert.Equal(AiInboxOutcomes.ProviderUnavailable, outcome);
        Assert.Empty(world.Sessions.All);
        Assert.Null(world.Conversations.StartedAgent);
        Assert.Empty(world.Jobs.Added);
    }

    // Spec 2026-10-26 D-1, que reemplazó D-M16 también para los agentes: cualquier entrante de una conversación abierta y sin asesor, no
    // sólo el que la abrió. Antes el agente se quedaba callado en una conversación que ya estaba abierta sin dueño (producción, 2026-11-17).
    [Fact]
    public async Task AnInboundInAnOpenUnassignedConversationStartsTheAgentEvenIfItDidNotOpenIt()
    {
        var world = new World();
        var agent = PublishedAgent();
        world.Sessions.Serving = agent;
        world.Conversations.State = State();
        world.Credentials.Available.Add("anthropic");

        var outcome = await world.Inbound().ProcessAsync(Inbound(), TestContext.Current.CancellationToken);

        Assert.Equal(AiInboxOutcomes.Started, outcome);
        Assert.Equal(agent.Id, world.Conversations.StartedAgent);
        Assert.Single(world.Sessions.All);
    }

    // D-13b y D-6: una pausa Handoff (el agente la pasó a la bandeja) o Manual frena al bot; la regla la tiene Conversation.StartAutomation,
    // que rechaza el arranque. Sin esto, el siguiente mensaje del cliente devolvería al agente la conversación que acaba de traspasar.
    [Fact]
    public async Task AConversationTheDomainRefusesToStartIsNotTakenAndSendsNothing()
    {
        var world = new World();
        world.Sessions.Serving = PublishedAgent(greeting: "¡Hola!");
        world.Conversations.State = State("Paused", "Agent");
        world.Conversations.NextStartFailure = new AgentBlockSessionProcessorTests.TestDomainException("messaging.automation.not_available");
        world.Credentials.Available.Add("anthropic");

        var outcome = await world.Inbound().ProcessAsync(Inbound(), TestContext.Current.CancellationToken);

        Assert.Equal(AiInboxOutcomes.Skipped, outcome);
        Assert.Null(world.Conversations.StartedAgent);
        Assert.Empty(world.Sessions.All);
        Assert.Empty(world.Outbound.Sends);
        Assert.Empty(world.Jobs.Added);
    }

    [Fact]
    public async Task OutsideHoursOnlyStartsWhenTheConnectionIsClosed()
    {
        var world = new World();
        world.Sessions.Serving = PublishedAgent(trigger: "OutsideHours");
        world.Conversations.State = State();
        world.Credentials.Available.Add("anthropic");
        world.Hours.Open = true;

        Assert.Equal(AiInboxOutcomes.Skipped, await world.Inbound().ProcessAsync(Inbound(), TestContext.Current.CancellationToken));

        world.Hours.Open = false;
        Assert.Equal(AiInboxOutcomes.Started, await world.Inbound().ProcessAsync(Inbound(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ACustomerTurnInALiveSessionMovesThePendingJob()
    {
        var world = new World();
        var agent = PublishedAgent();
        var session = world.Sessions.Live(agent);
        world.Conversations.State = State("Active", "Agent", agent.Id);
        var pending = ModelJob.ForSession(Guid.CreateVersion7(), Tenant, session.Id, 1, Now, Now);
        world.Sessions.Pending = pending;

        var outcome = await world.Inbound().ProcessAsync(Inbound(), TestContext.Current.CancellationToken);

        Assert.Equal(AiInboxOutcomes.Continued, outcome);
        Assert.Equal(session.NextSeq - 1, pending.UpToSeq);
        Assert.Empty(world.Jobs.Added);
    }

    [Fact]
    public async Task TheSameRowTwiceIsADuplicate()
    {
        var world = new World();
        var agent = PublishedAgent();
        var session = world.Sessions.Live(agent);
        world.Conversations.State = State("Active", "Agent", agent.Id);
        var item = Inbound();
        session.MarkInbound(item.MessageId);

        Assert.Equal(AiInboxOutcomes.Duplicate, await world.Inbound().ProcessAsync(item, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnAdvisorWhoTookTheConversationInterruptsTheSession()
    {
        var world = new World();
        var agent = PublishedAgent();
        var session = world.Sessions.Live(agent);
        world.Conversations.State = State("Paused", "Agent", agent.Id);

        var outcome = await world.Inbound().ProcessAsync(Inbound(), TestContext.Current.CancellationToken);

        Assert.Equal(AiInboxOutcomes.Interrupted, outcome);
        Assert.Equal(AgentSessionStatus.Interrupted, session.Status);
        Assert.Empty(world.Jobs.Added);
    }

    // §6.8: con el módulo apagado la conversación pasa a la bandeja sin asignar con causa ModuleDisabled y el proveedor.
    [Fact]
    public async Task WithTheModuleOffTheSessionEndsAndTheConversationIsHandedOff()
    {
        var world = new World();
        var agent = PublishedAgent(provider: "openai");
        var session = world.Sessions.Live(agent);
        world.Conversations.State = State("Active", "Agent", agent.Id);
        world.Modules.Enabled = false;

        var outcome = await world.Inbound().ProcessAsync(Inbound(), TestContext.Current.CancellationToken);

        Assert.Equal(AiInboxOutcomes.ModuleDisabled, outcome);
        Assert.Equal(AgentSessionStatus.Interrupted, session.Status);
        var handoff = Assert.Single(world.Conversations.Handoffs);
        Assert.Equal((HandoffCauseKind.ModuleDisabled, "openai", (Guid?)null), (handoff.Cause.Kind, handoff.Cause.Provider, handoff.AssignTo));
    }

    [Fact]
    public async Task ALockedConversationIsRetriedLater()
    {
        var world = new World();
        world.UnitOfWork.LockFree = false;

        Assert.Equal(AiInboxOutcomes.Locked, await world.Inbound().ProcessAsync(Inbound(), TestContext.Current.CancellationToken));
        Assert.Single(world.Inbox.Retried);
    }

    [Fact]
    public async Task AHeldReplyIsSentAndCounted()
    {
        var world = new World();
        var agent = PublishedAgent();
        var session = world.Sessions.Live(agent);
        world.Conversations.State = State("Active", "Agent", agent.Id);
        var job = HeldJob(session, """{"type":"replied","text":"Claro, te ayudo.","sources":["faq-1"]}""");

        await world.Worker().ProcessAsync(job, TestContext.Current.CancellationToken);

        Assert.Equal("Claro, te ayudo.", Assert.Single(world.Outbound.Sends).Text);
        Assert.Equal(AiDeterministicIds.ReplyClientId(job.Id, 0), world.Outbound.Sends[0].ClientId);
        // El turno del cliente lo pone Live(); la respuesta agrega exactamente un turno del agente.
        Assert.Equal([AgentTurnRoleKind.Customer, AgentTurnRoleKind.Agent], world.Sessions.Turns.Select(turn => turn.Role));
        Assert.Equal((ModelJobStatus.Done, 1), (job.Status, session.RepliesWithoutHuman));
        Assert.True(session.IsLive);
    }

    // D-A15 y Review Focus 1: la persona que tomó la conversación mientras el modelo pensaba gana; nada sale.
    [Fact]
    public async Task AReplyIsNotSentWhenAPersonTookTheConversation()
    {
        var world = new World();
        var agent = PublishedAgent();
        var session = world.Sessions.Live(agent);
        world.Conversations.State = State("Paused", "Agent", agent.Id);
        var job = HeldJob(session, """{"type":"replied","text":"Claro."}""");

        await world.Worker().ProcessAsync(job, TestContext.Current.CancellationToken);

        Assert.Empty(world.Outbound.Sends);
        Assert.Equal(AgentSessionStatus.Interrupted, session.Status);
        Assert.Equal(ModelJobStatus.Discarded, job.Status);
    }

    [Fact]
    public async Task ReachingMaxRepliesHandsOffWithTheProvider()
    {
        var world = new World();
        var agent = PublishedAgent(maxReplies: 1);
        var session = world.Sessions.Live(agent);
        world.Conversations.State = State("Active", "Agent", agent.Id);

        await world.Worker().ProcessAsync(HeldJob(session, """{"type":"replied","text":"Listo."}"""), TestContext.Current.CancellationToken);

        var handoff = Assert.Single(world.Conversations.Handoffs);
        Assert.Equal(
            (HandoffCauseKind.AgentLimitReached, AgentHandoffCodes.MaxRepliesWithoutHuman, "anthropic"),
            (handoff.Cause.Kind, handoff.Cause.Code, handoff.Cause.Provider));
        Assert.Equal(AgentSessionStatus.HandedOff, session.Status);
    }

    // Review Focus 6: un error de la cuenta del proveedor termina en traspaso, nunca en silencio.
    [Fact]
    public async Task AProviderFailureHandsOffWithItsCodeAndProvider()
    {
        var world = new World();
        var agent = PublishedAgent(provider: "openai");
        var session = world.Sessions.Live(agent);
        world.Conversations.State = State("Active", "Agent", agent.Id);
        // La forma que escribe StoredOutcome.Write: opciones Web sin conversor de enums, así que kind y failureReason viajan como números.
        var job = HeldJob(
            session,
            $$"""{"type":"failed","kind":{{(int)AgentFailureKind.ProviderFailed}},"failureReason":{{(int)AgentFailureReason.ProviderError}},"code":"ai.provider.quota_exhausted"}""");

        await world.Worker().ProcessAsync(job, TestContext.Current.CancellationToken);

        var handoff = Assert.Single(world.Conversations.Handoffs);
        Assert.Equal((HandoffCauseKind.ProviderFailed, "ai.provider.quota_exhausted", "openai"), (handoff.Cause.Kind, handoff.Cause.Code, handoff.Cause.Provider));
        Assert.Equal((AgentSessionStatus.Failed, ModelJobStatus.Failed), (session.Status, job.Status));
    }

    // D-A18: con la ventana cerrada no sale nada y una persona decide con plantilla.
    [Fact]
    public async Task AClosedWindowHandsOffWithoutSending()
    {
        var world = new World();
        var agent = PublishedAgent();
        var session = world.Sessions.Live(agent);
        world.Conversations.State = State("Active", "Agent", agent.Id, windowOpen: false);

        await world.Worker().ProcessAsync(HeldJob(session, """{"type":"replied","text":"Hola."}"""), TestContext.Current.CancellationToken);

        Assert.Empty(world.Outbound.Sends);
        Assert.Equal(HandoffCauseKind.WindowClosed, Assert.Single(world.Conversations.Handoffs).Cause.Kind);
        Assert.Equal(AgentSessionStatus.WindowClosed, session.Status);
    }

    // D-A14: el cliente siguió escribiendo; esta respuesta se descarta y el siguiente trabajo responde todo junto.
    [Fact]
    public async Task ANewerCustomerTurnSupersedesTheReply()
    {
        var world = new World();
        var agent = PublishedAgent();
        var session = world.Sessions.Live(agent);
        world.Conversations.State = State("Active", "Agent", agent.Id);
        var job = HeldJob(session, """{"type":"replied","text":"Hola."}""");
        world.Sessions.Turns.Add(session.AppendTurn(AgentTurnRoleKind.Customer, Guid.CreateVersion7(), null, [], false, Now));

        await world.Worker().ProcessAsync(job, TestContext.Current.CancellationToken);

        Assert.Empty(world.Outbound.Sends);
        Assert.Equal((ModelJobStatus.Discarded, 1), (job.Status, session.SupersededInRow));
    }

    // P-4b: el traspaso después del borrador de cotización va a su asesora, no al destino configurado, y queda auditado.
    [Fact]
    public async Task AQuotationDraftHandoffGoesToTheAdvisorAndIsAudited()
    {
        var world = new World();
        var agent = PublishedAgent();
        var session = world.Sessions.Live(agent);
        world.Conversations.State = State("Active", "Agent", agent.Id);
        var advisor = Guid.CreateVersion7();
        var job = HeldJob(session, $$"""{"type":"handed_off","reason":"quotation_draft_created","memberId":"{{advisor}}"}""");

        await world.Worker().ProcessAsync(job, TestContext.Current.CancellationToken);

        var handoff = Assert.Single(world.Conversations.Handoffs);
        Assert.Equal(
            (HandoffCauseKind.AgentRequested, AgentHandoffCodes.QuotationDraftCreated, (Guid?)advisor),
            (handoff.Cause.Kind, handoff.Cause.Code, handoff.AssignTo));
        Assert.Equal((AgentSessionStatus.HandedOff, ModelJobStatus.Done), (session.Status, job.Status));
        Assert.Equal((Tenant, AiAuditActions.QuotationDraftCreated, agent.Id), Assert.Single(world.Audit.Entries));
    }

    // El borrador ya existe: un turno nuevo del cliente no descarta el traspaso, o el siguiente trabajo crearía otro borrador.
    [Fact]
    public async Task AQuotationDraftHandoffIsNeverSuperseded()
    {
        var world = new World();
        var agent = PublishedAgent();
        var session = world.Sessions.Live(agent);
        world.Conversations.State = State("Active", "Agent", agent.Id);
        var job = HeldJob(session, $$"""{"type":"handed_off","reason":"quotation_draft_created","memberId":"{{Guid.CreateVersion7()}}"}""");
        world.Sessions.Turns.Add(session.AppendTurn(AgentTurnRoleKind.Customer, Guid.CreateVersion7(), null, [], false, Now));

        await world.Worker().ProcessAsync(job, TestContext.Current.CancellationToken);

        Assert.Single(world.Conversations.Handoffs);
        Assert.Equal((ModelJobStatus.Done, 0), (job.Status, session.SupersededInRow));
    }

    // Una escritura de la Agenda ya ocurrió: un turno nuevo del cliente no descarta la confirmación, o el siguiente trabajo (otra llave)
    // podría reservar de nuevo.
    [Fact]
    public async Task AReplyAfterASchedulingWriteIsNeverSuperseded()
    {
        var world = new World();
        var agent = PublishedAgent();
        var session = world.Sessions.Live(agent);
        world.Conversations.State = State("Active", "Agent", agent.Id);
        var job = HeldJob(session, """{"type":"replied","text":"Listo, tu cita quedó para el martes.","wrote":true}""");
        world.Sessions.Turns.Add(session.AppendTurn(AgentTurnRoleKind.Customer, Guid.CreateVersion7(), null, [], false, Now));

        await world.Worker().ProcessAsync(job, TestContext.Current.CancellationToken);

        Assert.Single(world.Outbound.Sends);
        Assert.Equal((ModelJobStatus.Done, 0), (job.Status, session.SupersededInRow));
    }

    // Revisión final W1: un traspaso de herramienta o un fallo después de una escritura exitosa tampoco se descartan.
    [Theory]
    [InlineData("""{"type":"handed_off","reason":"appointment_change_blocked","wrote":true}""", 1)]
    [InlineData("""{"type":"failed","kind":0,"failureReason":2,"wrote":true}""", 1)]
    [InlineData("""{"type":"failed","kind":0,"failureReason":2}""", 0)]
    public async Task AToolHandoffOrFailureAfterAWriteIsNeverSuperseded(string stored, int handedOff)
    {
        var world = new World();
        var agent = PublishedAgent();
        var session = world.Sessions.Live(agent);
        world.Conversations.State = State("Active", "Agent", agent.Id);
        var job = HeldJob(session, stored);
        world.Sessions.Turns.Add(session.AppendTurn(AgentTurnRoleKind.Customer, Guid.CreateVersion7(), null, [], false, Now));

        await world.Worker().ProcessAsync(job, TestContext.Current.CancellationToken);

        // Sin "wrote" (un resultado guardado antes de este campo) se lee false: se descarta como siempre.
        Assert.Equal(handedOff == 0, job.Status == ModelJobStatus.Discarded);
    }

    // Lo contrario: sin escritura, el mismo turno nuevo sí descarta (la prueba de arriba no pasa por no mirar el turno).
    [Fact]
    public async Task AReplyWithoutAWriteIsStillSuperseded()
    {
        var world = new World();
        var agent = PublishedAgent();
        var session = world.Sessions.Live(agent);
        world.Conversations.State = State("Active", "Agent", agent.Id);
        var job = HeldJob(session, """{"type":"replied","text":"Hola."}""");
        world.Sessions.Turns.Add(session.AppendTurn(AgentTurnRoleKind.Customer, Guid.CreateVersion7(), null, [], false, Now));

        await world.Worker().ProcessAsync(job, TestContext.Current.CancellationToken);

        Assert.Empty(world.Outbound.Sends);
        Assert.Equal(ModelJobStatus.Discarded, job.Status);
    }

    // Spec 2026-11-23 D-3/D-6: las dos causas de traspaso de la Agenda llegan al destino que el agente tiene configurado (persona o sin
    // asignar), no a una asesora de cotización.
    [Theory]
    [InlineData(AgentHandoffCodes.AppointmentChangeBlocked, true)]
    [InlineData(AgentHandoffCodes.AppointmentChangeBlocked, false)]
    [InlineData(AgentHandoffCodes.AppointmentCustomerUnavailable, true)]
    [InlineData(AgentHandoffCodes.AppointmentCustomerUnavailable, false)]
    public async Task AppointmentHandoffsGoToTheConfiguredDestination(string code, bool toMember)
    {
        var world = new World();
        var destination = toMember ? Guid.CreateVersion7() : (Guid?)null;
        var agent = PublishedAgent(destinationMemberId: destination);
        var session = world.Sessions.Live(agent);
        world.Conversations.State = State("Active", "Agent", agent.Id);
        var job = HeldJob(session, $$"""{"type":"handed_off","reason":"{{code}}"}""");

        await world.Worker().ProcessAsync(job, TestContext.Current.CancellationToken);

        var handoff = Assert.Single(world.Conversations.Handoffs);
        Assert.Equal((HandoffCauseKind.AgentRequested, code, destination), (handoff.Cause.Kind, handoff.Cause.Code, handoff.AssignTo));
        Assert.Equal((AgentSessionStatus.HandedOff, ModelJobStatus.Done), (session.Status, job.Status));
    }

    // En modo bloque el flujo decide a quién va: estas causas vuelven por «sin respuesta» con su código como motivo (no por «humano»).
    [Theory]
    [InlineData(AgentHandoffCodes.AppointmentChangeBlocked)]
    [InlineData(AgentHandoffCodes.AppointmentCustomerUnavailable)]
    public void AppointmentHandoffsLeaveABlockByNoAnswerWithTheirCode(string code)
    {
        var result = AgentBlockReturn.For(new AgentHandoffCause(HandoffCauseKind.AgentRequested, code, "anthropic"));

        Assert.Equal((AgentBlockOutputs.NoAnswer, code), (result.Output, result.Reason));
    }

    // P-4: sin cliente que se pueda cotizar, el traspaso va al destino configurado y no se audita un borrador que no existe.
    [Fact]
    public async Task ACustomerNotReadyHandsOffToTheConfiguredDestination()
    {
        var world = new World();
        var agent = PublishedAgent();
        var session = world.Sessions.Live(agent);
        world.Conversations.State = State("Active", "Agent", agent.Id);

        await world.Worker().ProcessAsync(
            HeldJob(session, """{"type":"handed_off","reason":"quotation_customer_not_ready"}"""), TestContext.Current.CancellationToken);

        var handoff = Assert.Single(world.Conversations.Handoffs);
        Assert.Equal((AgentHandoffCodes.QuotationCustomerNotReady, (Guid?)null), (handoff.Cause.Code, handoff.AssignTo));
        Assert.Empty(world.Audit.Entries);
    }

    // Revisión final, hallazgo 2: un entrante que llega mientras se confirma sube conversations.version y el traspaso de Mensajería sale como
    // RequestConcurrencyException. El commit se deshace y el trabajo se retoma pronto con la respuesta guardada, sin quedar colgado.
    [Fact]
    public async Task AConcurrencyConflictOnTheHandoffRetriesTheStoredResultSoon()
    {
        var world = new World();
        var agent = PublishedAgent();
        var session = world.Sessions.Live(agent);
        world.Conversations.State = State("Active", "Agent", agent.Id);
        var job = HeldJob(session, """{"type":"handed_off","reason":"customer_asked"}""");
        world.Conversations.NextHandoffFailure = new RequestConcurrencyException("concurrency.conflict", "The conversation changed.");

        await world.Worker().ProcessAsync(job, TestContext.Current.CancellationToken);

        Assert.Equal([(job.Id, Now + AgentSessionJobProcessor.LockedRetryDelay)], world.Jobs.HeldRetries);
        Assert.Empty(world.Conversations.Handoffs);
        Assert.Equal(ModelJobStatus.Pending, job.Status);

        await world.Worker().ProcessAsync(job, TestContext.Current.CancellationToken);

        Assert.Equal(HandoffCauseKind.AgentRequested, Assert.Single(world.Conversations.Handoffs).Cause.Kind);
        Assert.Equal((AgentSessionStatus.HandedOff, ModelJobStatus.Done), (session.Status, job.Status));
    }

    // Revisión final, hallazgo 2: la respuesta del modelo se guarda antes del commit. El borrador ya se escribió en Quotations y el commit
    // falló: el trabajo retomado no vuelve a llamar al modelo (ni a pagar, ni a crear otro borrador) y termina el traspaso a la asesora.
    [Fact]
    public async Task ARerunAfterTheDraftWasWrittenNeverCallsTheModelAgain()
    {
        var world = new World();
        var agent = QuotingAgent();
        var session = world.Sessions.Live(agent);
        world.Conversations.State = State("Active", "Agent", agent.Id, customerId: Customer);
        world.Credentials.Available.Add("anthropic");
        world.Model.Enqueue(DraftCall());
        world.Conversations.NextHandoffFailure = new RequestConcurrencyException("concurrency.conflict", "The conversation changed.");
        var job = ModelJob.ForSession(Guid.CreateVersion7(), Tenant, session.Id, session.NextSeq - 1, Now, Now);

        await world.Worker().ProcessAsync(job, TestContext.Current.CancellationToken);

        Assert.NotNull(job.ResultJson);
        Assert.Single(world.Jobs.HeldRetries);

        await world.Worker().ProcessAsync(job, TestContext.Current.CancellationToken);

        Assert.Single(world.Model.Requests);
        Assert.Single(world.Drafts.Requests);
        var handoff = Assert.Single(world.Conversations.Handoffs);
        Assert.Equal((AgentHandoffCodes.QuotationDraftCreated, (Guid?)Advisor), (handoff.Cause.Code, handoff.AssignTo));
        Assert.Equal(ModelJobStatus.Done, job.Status);
    }

    // Revisión final, hallazgo 2: un trabajo que se reclamó más de MaxClaims veces (falla siempre, o el proceso muere siempre) no se retoma
    // más: traspaso, sin llamar al modelo.
    [Fact]
    public async Task AJobClaimedTooManyTimesHandsOffWithoutCallingTheModel()
    {
        var world = new World();
        var agent = PublishedAgent(provider: "openai");
        var session = world.Sessions.Live(agent);
        world.Conversations.State = State("Active", "Agent", agent.Id);
        var job = ModelJob.ForSession(Guid.CreateVersion7(), Tenant, session.Id, session.NextSeq - 1, Now, Now);
        typeof(ModelJob).GetProperty(nameof(ModelJob.Attempts))!.SetValue(job, ModelJob.MaxClaims + 1);

        await world.Worker().ProcessAsync(job, TestContext.Current.CancellationToken);

        Assert.Empty(world.Model.Requests);
        var handoff = Assert.Single(world.Conversations.Handoffs);
        Assert.Equal((HandoffCauseKind.AgentFailed, AgentHandoffCodes.JobTimedOut, "openai"), (handoff.Cause.Kind, handoff.Cause.Code, handoff.Cause.Provider));
        Assert.Equal((AgentSessionStatus.Failed, ModelJobStatus.Failed), (session.Status, job.Status));
    }

    // Revisión final, hallazgo 4: el agente se borró con la sesión viva (la carrera con DeleteAgentHandler). El trabajo no se descarta en
    // silencio: la conversación pasa a una persona y la sesión termina.
    [Fact]
    public async Task AJobWhoseAgentWasDeletedHandsOff()
    {
        var world = new World();
        var agent = PublishedAgent();
        var session = world.Sessions.Live(agent);
        world.Sessions.Forget(agent);
        world.Conversations.State = State("Active", "Agent", agent.Id);
        var job = ModelJob.ForSession(Guid.CreateVersion7(), Tenant, session.Id, session.NextSeq - 1, Now, Now);

        await world.Worker().ProcessAsync(job, TestContext.Current.CancellationToken);

        Assert.Empty(world.Model.Requests);
        Assert.Equal(HandoffCauseKind.AgentFailed, Assert.Single(world.Conversations.Handoffs).Cause.Kind);
        Assert.Equal((AgentSessionStatus.Failed, ModelJobStatus.Failed), (session.Status, job.Status));
    }

    // Revisión final, hallazgo 4: una conversación que una persona le devolvió al agente (Active, sin sesión viva) ya es del agente. Sin la
    // cuenta de su proveedor no se abre sesión, y la conversación pasa a una persona en vez de quedar con un agente que no contesta.
    [Fact]
    public async Task AReturnedConversationWithoutTheProviderAccountIsHandedOff()
    {
        var world = new World();
        var agent = PublishedAgent(provider: "openai");
        world.Sessions.Serving = agent;
        world.Conversations.State = State("Active", "Agent", agent.Id);

        var outcome = await world.Inbound().ProcessAsync(Inbound(), TestContext.Current.CancellationToken);

        Assert.Equal(AiInboxOutcomes.ProviderUnavailable, outcome);
        Assert.Empty(world.Sessions.All);
        var handoff = Assert.Single(world.Conversations.Handoffs);
        Assert.Equal(
            (HandoffCauseKind.ProviderFailed, AiErrorCodes.ProviderNotConfigured, "openai"), (handoff.Cause.Kind, handoff.Cause.Code, handoff.Cause.Provider));
    }

    // Revisión final, hallazgo 4: un agente pausado no abre sesiones nuevas (D-A6), tampoco sobre una conversación devuelta.
    [Fact]
    public async Task AReturnedConversationOfAPausedAgentIsHandedOffWithoutASession()
    {
        var world = new World();
        var agent = PublishedAgent();
        agent.Pause(Now);
        world.Sessions.Serving = agent;
        world.Conversations.State = State("Active", "Agent", agent.Id);
        world.Credentials.Available.Add("anthropic");

        var outcome = await world.Inbound().ProcessAsync(Inbound(), TestContext.Current.CancellationToken);

        Assert.Equal(AiInboxOutcomes.HandedOff, outcome);
        Assert.Empty(world.Sessions.All);
        Assert.Empty(world.Jobs.Added);
        Assert.Equal((HandoffCauseKind.AgentFailed, "anthropic"), (Assert.Single(world.Conversations.Handoffs).Cause.Kind, world.Conversations.Handoffs[0].Cause.Provider));
    }

    // Revisión final, hallazgo 4: el agente que tenía la conversación ya no existe.
    [Fact]
    public async Task AReturnedConversationOfADeletedAgentIsHandedOff()
    {
        var world = new World();
        world.Conversations.State = State("Active", "Agent", Guid.CreateVersion7());
        world.Credentials.Available.Add("anthropic");

        var outcome = await world.Inbound().ProcessAsync(Inbound(), TestContext.Current.CancellationToken);

        Assert.Equal(AiInboxOutcomes.HandedOff, outcome);
        Assert.Empty(world.Sessions.All);
        Assert.Equal(HandoffCauseKind.AgentFailed, Assert.Single(world.Conversations.Handoffs).Cause.Kind);
    }

    // Revisión final, hallazgo 4 (§6.8): con el módulo apagado, la conversación del agente sin sesión viva también pasa a la bandeja.
    [Fact]
    public async Task WithTheModuleOffAnAgentConversationWithoutSessionIsHandedOff()
    {
        var world = new World();
        var agent = PublishedAgent(provider: "openai");
        world.Sessions.Serving = agent;
        world.Conversations.State = State("Active", "Agent", agent.Id);
        world.Modules.Enabled = false;

        var outcome = await world.Inbound().ProcessAsync(Inbound(), TestContext.Current.CancellationToken);

        Assert.Equal(AiInboxOutcomes.ModuleDisabled, outcome);
        var handoff = Assert.Single(world.Conversations.Handoffs);
        Assert.Equal((HandoffCauseKind.ModuleDisabled, "openai"), (handoff.Cause.Kind, handoff.Cause.Provider));
    }

    // Revisión final, hallazgo 5: la señal ReleaseRun de una conversación de un agente (una persona la tomó) cierra la sesión y descarta su
    // trabajo pendiente, que si no el worker igual reclamaría.
    [Fact]
    public async Task AReleaseRunEndsTheAgentSessionAndDiscardsItsPendingJob()
    {
        var world = new World();
        var agent = PublishedAgent();
        var session = world.Sessions.Live(agent);
        world.Conversations.State = State("Paused", "Agent", agent.Id);
        var pending = ModelJob.ForSession(Guid.CreateVersion7(), Tenant, session.Id, 1, Now, Now);
        world.Sessions.Pending = pending;

        var outcome = await world.Inbound().ProcessAsync(Inbound() with { ReleaseRun = true }, TestContext.Current.CancellationToken);

        Assert.Equal(AiInboxOutcomes.Interrupted, outcome);
        Assert.Equal(AgentSessionStatus.Interrupted, session.Status);
        Assert.Equal(ModelJobStatus.Discarded, pending.Status);
        Assert.Empty(world.Jobs.Added);
        Assert.Empty(world.Conversations.Handoffs);
    }

    // Revisión final, hallazgo 5: el worker mira que la conversación siga siendo del agente antes de llamar al modelo. Si una persona la tomó
    // mientras el trabajo esperaba, se descarta sin pagar la llamada.
    [Fact]
    public async Task AJobIsDiscardedBeforeCallingTheModelWhenAPersonTookTheConversation()
    {
        var world = new World();
        var agent = PublishedAgent();
        var session = world.Sessions.Live(agent);
        world.Conversations.State = State("Paused", "Agent", agent.Id);
        world.Credentials.Available.Add("anthropic");
        var job = ModelJob.ForSession(Guid.CreateVersion7(), Tenant, session.Id, session.NextSeq - 1, Now, Now);

        await world.Worker().ProcessAsync(job, TestContext.Current.CancellationToken);

        Assert.Empty(world.Model.Requests);
        Assert.Empty(world.Outbound.Sends);
        Assert.Equal(ModelJobStatus.Discarded, job.Status);
    }

    // Owner, 2026-11-18: pausar un agente corta también sus sesiones vivas. El trabajo pendiente no llama al modelo ni envía: la
    // conversación pasa a una persona y la sesión termina.
    [Fact]
    public async Task AJobOfAPausedAgentIsNotAnsweredAndTheConversationIsHandedOff()
    {
        var world = new World();
        var agent = PublishedAgent();
        var session = world.Sessions.Live(agent);
        world.Conversations.State = State("Active", "Agent", agent.Id);
        world.Credentials.Available.Add("anthropic");
        agent.Pause(Now);
        var job = ModelJob.ForSession(Guid.CreateVersion7(), Tenant, session.Id, session.NextSeq - 1, Now, Now);

        await world.Worker().ProcessAsync(job, TestContext.Current.CancellationToken);

        Assert.Empty(world.Model.Requests);
        Assert.Empty(world.Outbound.Sends);
        var handoff = Assert.Single(world.Conversations.Handoffs);
        Assert.Equal((HandoffCauseKind.AgentFailed, AgentHandoffCodes.AgentPaused), (handoff.Cause.Kind, handoff.Cause.Code));
        Assert.Equal(AgentSessionStatus.Failed, session.Status);
        Assert.Equal(ModelJobStatus.Failed, job.Status);
    }

    private static AiModelResponse DraftCall() =>
        new(
            null,
            [new AiToolCall(
                "call_q", AgentToolNames.CreateQuotationDraft,
                System.Text.Json.JsonSerializer.Serialize(new { lines = new[] { new { variantId = Guid.CreateVersion7(), quantity = 1 } }, note = (string?)null }))],
            [new AiOpaqueBlock("opaque")], AiStopReason.ToolUse, new AiModelUsage(100, 20, null, null), "claude-opus-5-5", "req_1");

    private static ModelJob HeldJob(AgentSession session, string resultJson)
    {
        var job = ModelJob.ForSession(Guid.CreateVersion7(), Tenant, session.Id, session.NextSeq - 1, Now, Now);
        job.HoldResult(resultJson, Now);
        return job;
    }

    internal sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    internal sealed class FakeAudit : IAiAuditRecorder
    {
        public List<(Guid Tenant, string Action, Guid Agent)> Entries { get; } = [];

        public void Record(Guid tenantId, Guid actorId, string action, Guid agentId, DateTimeOffset at) => Entries.Add((tenantId, action, agentId));

        public void RecordByAgent(Guid tenantId, string action, Guid agentId, DateTimeOffset at) => Entries.Add((tenantId, action, agentId));

        public void RecordCopilot(Guid tenantId, Guid? actorId, string action, string resourceType, Guid resourceId, DateTimeOffset at) =>
            Entries.Add((tenantId, action, resourceId));
    }

    internal sealed class FakeUnitOfWork : IAiSessionUnitOfWork
    {
        public bool LockFree { get; set; } = true;

        public int Saves { get; private set; }

        public Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IAsyncDisposable>(new Nothing());

        public Task<bool> TryLockConversationAsync(Guid conversationId, CancellationToken cancellationToken) => Task.FromResult(LockFree);

        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            Saves++;
            return Task.CompletedTask;
        }

        private sealed class Nothing : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    internal sealed class FakeSessions : IAgentSessionRepository
    {
        public List<AgentSession> All { get; } = [];

        public List<AgentTurn> Turns { get; } = [];

        public Agent? Serving { get; set; }

        public ModelJob? Pending { get; set; }

        private readonly List<Agent> _agents = [];

        /// <summary>El agente deja de existir (se borró) y sus sesiones quedan.</summary>
        public void Forget(Agent agent)
        {
            _agents.Remove(agent);
            if (Serving == agent)
            {
                Serving = null;
            }
        }

        /// <summary>Spec 2026-11-17: una sesión viva de un bloque de chatbot, con un turno del cliente, que se movió por última vez en
        /// <paramref name="updatedAt"/>.</summary>
        public AgentSession LiveBlock(Agent agent, Guid delegationId, int? idleMinutes = null, DateTimeOffset? updatedAt = null)
        {
            _agents.Add(agent);
            var at = updatedAt ?? Now.AddMinutes(-5);
            var session = AgentSession.OpenForBlock(
                AiDeterministicIds.SessionForDelegation(delegationId), Tenant, Conversation, Connection, agent.Id, agent.PublishedVersionId!.Value,
                delegationId, Guid.CreateVersion7(), Guid.CreateVersion7(), idleMinutes, at);
            Turns.Add(session.AppendTurn(AgentTurnRoleKind.Customer, Guid.CreateVersion7(), null, [], false, at));
            All.Add(session);
            return session;
        }

        public AgentSession Live(Agent agent)
        {
            _agents.Add(agent);
            var session = AgentSession.Open(Guid.CreateVersion7(), Tenant, Conversation, Connection, agent.Id, agent.PublishedVersionId!.Value, Now.AddMinutes(-5));
            Turns.Add(session.AppendTurn(AgentTurnRoleKind.Customer, Guid.CreateVersion7(), null, [], false, Now.AddMinutes(-5)));
            All.Add(session);
            return session;
        }

        public Task<AgentSession?> FindLiveForUpdateAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) =>
            Task.FromResult(All.FirstOrDefault(session => session.ConversationId == conversationId && session.IsLive));

        public Task<AgentSession?> FindLatestAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) =>
            Task.FromResult(All.LastOrDefault(session => session.ConversationId == conversationId));

        public Task<AgentSession?> FindAsync(Guid tenantId, Guid sessionId, CancellationToken cancellationToken) =>
            Task.FromResult(All.FirstOrDefault(session => session.Id == sessionId));

        public void Add(AgentSession session) => All.Add(session);

        public void AddTurn(AgentTurn turn) => Turns.Add(turn);

        public Task<IReadOnlyList<AgentTurn>> ListTurnsAsync(Guid sessionId, int upToSeq, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AgentTurn>>([.. Turns.Where(turn => turn.SessionId == sessionId && turn.Seq <= upToSeq)]);

        public Task<bool> HasCustomerTurnAfterAsync(Guid sessionId, int seq, CancellationToken cancellationToken) =>
            Task.FromResult(Turns.Any(turn => turn.SessionId == sessionId && turn.Seq > seq && turn.Role == AgentTurnRoleKind.Customer));

        public Task<ModelJob?> FindPendingJobAsync(Guid sessionId, CancellationToken cancellationToken) => Task.FromResult(Pending);

        /// <summary>Todas las vivas como candidatas: el corte por sesión lo vuelve a mirar el barrido con el candado.</summary>
        public Task<IReadOnlyList<AgentSessionRef>> ListIdleAsync(DateTimeOffset now, TimeSpan defaultIdle, int batch, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AgentSessionRef>>(
                [.. All.Where(session => session.IsLive).Select(session => new AgentSessionRef(session.TenantId, session.ConversationId))]);

        public Task<IReadOnlyList<AgentSessionRef>> ListLiveAsync(Guid tenantId, int batch, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AgentSessionRef>>(
                [.. All.Where(session => session.IsLive && session.TenantId == tenantId).Select(session => new AgentSessionRef(session.TenantId, session.ConversationId))]);

        public Task<IReadOnlyList<Guid>> ListTenantsWithLiveSessionsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Guid>>([.. All.Where(session => session.IsLive).Select(session => session.TenantId).Distinct()]);

        public Task<bool> HasLiveSessionsAsync(Guid tenantId, Guid agentId, CancellationToken cancellationToken) =>
            Task.FromResult(All.Any(session => session.AgentId == agentId && session.IsLive));

        public Task<Agent?> FindServingAgentAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) => Task.FromResult(Serving);

        public Task<Agent?> FindAgentAsync(Guid tenantId, Guid agentId, CancellationToken cancellationToken) =>
            Task.FromResult(_agents.Append(Serving).FirstOrDefault(agent => agent?.Id == agentId));
    }

    internal sealed class FakeJobs : IAiModelJobs
    {
        public List<ModelJob> Added { get; } = [];

        public List<(Guid JobId, DateTimeOffset RetryAt)> HeldRetries { get; } = [];

        public void Add(ModelJob job) => Added.Add(job);

        public Task<ModelJob?> ClaimAsync(DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken) => Task.FromResult<ModelJob?>(null);

        public Task RetryHeldAsync(Guid jobId, DateTimeOffset retryAt, CancellationToken cancellationToken)
        {
            HeldRetries.Add((jobId, retryAt));
            return Task.CompletedTask;
        }
    }

    internal sealed class ScriptedModel : IAiModelClient
    {
        private readonly Queue<AiModelResponse> _script = new();

        public List<AiModelRequest> Requests { get; } = [];

        public void Enqueue(AiModelResponse response) => _script.Enqueue(response);

        public Task<AiModelResponse> CompleteAsync(AiModelRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_script.Dequeue());
        }
    }

    internal sealed class SingleResolver(IAiModelClient client) : IAiModelClientResolver
    {
        public IAiModelClient For(AiProvider provider) => client;
    }

    internal sealed class NoLedger : IAiRunLedger
    {
        public Task RecordUsageAsync(AiUsageRecord record, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RecordToolCallAsync(AiToolCallRecord record, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    internal sealed class FakeDrafts : IAiQuotationDrafts
    {
        public List<AiQuotationDraftRequest> Requests { get; } = [];

        public Task<AiQuotationDraftResult> CreateAsync(AiQuotationDraftRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(AiQuotationDraftResult.Created("COT-0001", Advisor));
        }
    }

    internal sealed class FakeAdvisors : IAiQuotationAdvisors
    {
        public Task<IReadOnlyList<AiMemberRef>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AiMemberRef>>([new AiMemberRef(Advisor, "Ana")]);

        public Task<bool> CanQuoteAsync(Guid tenantId, Guid memberId, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    internal sealed class EmptyCatalog : IAiModelCatalog
    {
        public AiProvider DefaultProvider => AiProviders.Anthropic;

        public IReadOnlyList<AiProviderModels> Providers => [];
    }

    internal sealed class NoQuickReplies : IAiQuickReplies
    {
        public Task<IReadOnlyList<AiQuickReply>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AiQuickReply>>([]);
    }

    internal sealed class UtcTenantClock : Modules.Tenancy.Application.ITenantClock
    {
        public Task<Modules.Tenancy.Application.TenantCalendar> GetAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult(new Modules.Tenancy.Application.TenantCalendar(Now, TimeZoneInfo.Utc));
    }

    internal sealed class FakeInbox : IAiInbox
    {
        public List<(Guid MessageId, string Outcome)> Completed { get; } = [];

        public List<Guid> Retried { get; } = [];

        public Task<IReadOnlyList<AiInboxItem>> ClaimAsync(DateTimeOffset now, int batch, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AiInboxItem>>([]);

        public Task<bool> CompleteAsync(Guid tenantId, Guid messageId, int attempts, string outcome, CancellationToken cancellationToken)
        {
            Completed.Add((messageId, outcome));
            return Task.FromResult(true);
        }

        public Task<bool> RetryLaterAsync(Guid tenantId, Guid messageId, int attempts, TimeSpan delay, CancellationToken cancellationToken)
        {
            Retried.Add(messageId);
            return Task.FromResult(true);
        }

        public Task<bool> IsInboundAsync(Guid tenantId, Guid messageId, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    internal sealed class FakeConversations : IAiConversations
    {
        public Task<AiDelegation?> ReadDelegationAsync(Guid tenantId, Guid delegationId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ReturnToChatbotAsync(Guid tenantId, Guid conversationId, Guid delegationId, string resultJson, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task InterruptOrphanDelegationAsync(Guid tenantId, Guid conversationId, Guid delegationId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public AiConversationState? State { get; set; }

        public Guid? StartedAgent { get; private set; }

        public List<(AgentHandoffCause Cause, Guid? AssignTo)> Handoffs { get; } = [];

        public Task<AiConversationState?> GetAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) => Task.FromResult(State);

        /// <summary>Lo que lanza el próximo arranque, una sola vez (la regla de pausa de <c>Conversation.StartAutomation</c>).</summary>
        public Exception? NextStartFailure { get; set; }

        public Task StartAsync(Guid tenantId, Guid conversationId, Guid agentId, CancellationToken cancellationToken)
        {
            if (NextStartFailure is { } failure)
            {
                NextStartFailure = null;
                throw failure;
            }

            StartedAgent = agentId;
            State = State! with { AutomationStatus = "Active", AutomationBotKind = "Agent", AutomationAgentId = agentId };
            return Task.CompletedTask;
        }

        /// <summary>Lo que lanza el próximo traspaso, una sola vez (una carrera con Mensajería).</summary>
        public Exception? NextHandoffFailure { get; set; }

        public Task HandoffAsync(Guid tenantId, Guid conversationId, AgentHandoffCause cause, Guid? assignToMemberId, CancellationToken cancellationToken)
        {
            if (NextHandoffFailure is { } failure)
            {
                NextHandoffFailure = null;
                throw failure;
            }

            Handoffs.Add((cause, assignToMemberId));
            return Task.CompletedTask;
        }

        public Task EndAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyDictionary<Guid, AiThreadMessage>> ReadMessagesAsync(
            Guid tenantId, IReadOnlyCollection<Guid> messageIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, AiThreadMessage>>(new Dictionary<Guid, AiThreadMessage>());
    }

    internal sealed class FakeCredentials : IAiProviderCredentials
    {
        public HashSet<string> Available { get; } = [];

        public Task<AiCredentialResolution> ResolveAsync(Guid tenantId, AiProvider provider, CancellationToken cancellationToken) =>
            Task.FromResult(Available.Contains(provider.Value)
                ? AiCredentialResolution.Resolved(new AiCredential(Guid.CreateVersion7(), "test-key"))
                : AiCredentialResolution.Unusable(AiCredentialUnavailableReason.NeedsAttention));

        public Task<IReadOnlyList<AiProviderConnectionState>> DescribeAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AiProviderConnectionState>>([]);

        public Task MarkNeedsAttentionAsync(Guid tenantId, Guid connectionId, string failureCode, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    internal sealed class FakeOutbound : IAiBotOutbound
    {
        public List<(Guid ClientId, string Text)> Sends { get; } = [];

        /// <summary>Lo que responde el próximo envío, una vez (Mensajería no pudo enviar); sin él, enviado.</summary>
        public AiSendResult? NextResult { get; set; }

        public Task<AiSendResult> SendTextAsync(
            Guid tenantId, Guid conversationId, Guid agentId, Guid clientId, string text, CancellationToken cancellationToken)
        {
            if (NextResult is { } next)
            {
                NextResult = null;
                return Task.FromResult(next);
            }

            Sends.Add((clientId, text));
            return Task.FromResult(new AiSendResult(AiSendStatus.Sent, Guid.CreateVersion7()));
        }
    }

    internal sealed class FakeModules : IAiModuleStatus
    {
        public bool Enabled { get; set; } = true;

        public Task<bool> IsEnabledAsync(Guid tenantId, CancellationToken cancellationToken) => Task.FromResult(Enabled);
    }

    internal sealed class FakeHours : IAiBusinessHours
    {
        public bool Open { get; set; } = true;

        public Task<bool> IsOpenAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) => Task.FromResult(Open);
    }
}
