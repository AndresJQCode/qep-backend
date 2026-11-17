using System.Text.Json;
using BuildingBlocks.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Modules.Ai.Application;
using Modules.Ai.Domain;
using static Modules.Ai.UnitTests.AgentSessionProcessorTests;

namespace Modules.Ai.UnitTests;

/// <summary>
/// Spec 2026-11-17 (agente IA en chatbots) §6.2–§6.5: la sesión del agente en modo bloque. La abre la fila <c>Delegated</c> de la cola (o el
/// primer entrante que llegue antes) y toda terminación que no sea una toma humana devuelve la conversación al chatbot por una salida, nunca
/// traspasa por su cuenta. Dobles en memoria de los puertos, como <see cref="AgentSessionProcessorTests"/>, cuyos dobles (y su tenant, su
/// conexión y su conversación) reusa.
/// </summary>
public sealed class AgentBlockSessionProcessorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid By = Guid.CreateVersion7();
    private static readonly Guid Chatbot = Guid.CreateVersion7();
    private static readonly Guid Run = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class World
    {
        public FakeUnitOfWork UnitOfWork { get; } = new();

        public FakeSessions Sessions { get; } = new();

        public FakeJobs Jobs { get; } = new();

        public FakeInbox Inbox { get; } = new();

        public BlockConversations Conversations { get; } = new();

        public FakeCredentials Credentials { get; } = new();

        public FakeOutbound Outbound { get; } = new();

        public FakeModules Modules { get; } = new();

        public FixedClock Clock { get; } = new(Now);

        public FakeAudit Audit { get; } = new();

        public FakeDelegationSweep DelegationSweep { get; } = new();

        public AiInboxProcessor Inbound() => new(
            UnitOfWork, Sessions, Jobs, Inbox, Conversations, new FakeHours(), Modules, Credentials, Outbound, AiSessionTimings.Default, Clock,
            NullLogger<AiInboxProcessor>.Instance);

        public AgentSessionJobProcessor Worker()
        {
            var limits = new AiRunLimits(16_000, TimeSpan.FromSeconds(120), 6, 8, 30, null);
            var runner = new AgentTurnRunner(
                new SingleResolver(new ScriptedModel()), [], new NoLedger(), Credentials, limits, Clock, NullLogger<AgentTurnRunner>.Instance);
            return new(
                UnitOfWork, Sessions, Jobs, Conversations, Outbound, Modules, Credentials, new EmptyCatalog(), new NoQuickReplies(),
                new UtcTenantClock(), new AgentPromptBuilder(limits), runner, Clock, NullLogger<AgentSessionJobProcessor>.Instance, Audit);
        }

        public AiSessionSweeper Sweeper() => new(UnitOfWork, Sessions, Conversations, Modules, DelegationSweep, AiSessionTimings.Default, Clock);
    }

    internal sealed class FakeDelegationSweep : IAiDelegationSweep
    {
        public int Orphans { get; set; }

        public List<DateTimeOffset> Swept { get; } = [];

        public Task<int> InterruptOrphansAsync(DateTimeOffset now, int batch, CancellationToken cancellationToken)
        {
            Swept.Add(now);
            return Task.FromResult(Orphans);
        }
    }

    private static Agent PublishedAgent(string provider = "anthropic", string? greeting = "¡Hola! Soy el asesor virtual.")
    {
        var config = AgentConfig.Default(new AgentModelRef(provider, provider == "anthropic" ? "claude-opus-5-5" : "gpt-6-luna")) with
        {
            Instructions = "Responde dudas.",
            Greeting = greeting,
        };
        var agent = Agent.Create(Tenant, "Asesor virtual", config, By, Now);
        agent.Publish([], By, Now);
        return agent;
    }

    private static string Brief(string firstTurn = "lastMessage", int idleMinutes = 45) =>
        JsonSerializer.Serialize(new { objective = "Resuelve dudas de precios.", context = "El cliente eligió «Precios».", firstTurn, idleMinutes });

    /// <summary>La delegación viva de la conversación, guardada en el doble; la conversación queda en manos del agente.</summary>
    private static AiDelegation Delegate(World world, Agent agent, Guid? lastInbound, string? brief = null, string status = "Live")
    {
        var delegation = new AiDelegation(
            Guid.CreateVersion7(), Conversation, Connection, Chatbot, Run, "ai-agent-1", agent.Id, lastInbound, brief ?? Brief(), status,
            Now.AddSeconds(-1));
        world.Conversations.Delegations[delegation.DelegationId] = delegation;
        world.Conversations.State = DelegatedState(agent.Id, delegation.DelegationId);
        world.Sessions.Serving = agent;
        return delegation;
    }

    private static AiConversationState DelegatedState(Guid agentId, Guid? delegationId, bool windowOpen = true) =>
        new("Open", null, "Active", "Agent", agentId, Connection, "WhatsApp", null, null, false, windowOpen, TimeSpan.FromHours(20), 4096,
            delegationId);

    /// <summary>La fila Delegated: su message_id no es un mensaje ni la delegación (B1.3).</summary>
    private static AiInboxItem DelegatedRow(AiDelegation delegation) =>
        new(Guid.CreateVersion7(), Tenant, Connection, Conversation, false, Now, 1, ReleaseRun: false, DelegationId: delegation.DelegationId);

    private static AiInboxItem InboundRow() =>
        new(Guid.CreateVersion7(), Tenant, Connection, Conversation, false, Now, 1, ReleaseRun: false);

    [Fact]
    public async Task ADelegatedRowOpensABlockSessionWithoutGreeting()
    {
        var world = new World();
        var agent = PublishedAgent();
        world.Credentials.Available.Add("anthropic");
        var delegation = Delegate(world, agent, lastInbound: Guid.CreateVersion7());

        var outcome = await world.Inbound().ProcessAsync(DelegatedRow(delegation), Ct);

        Assert.Equal(AiInboxOutcomes.Started, outcome);
        var session = Assert.Single(world.Sessions.All);
        Assert.Equal(AiDeterministicIds.SessionForDelegation(delegation.DelegationId), session.Id);
        Assert.Equal(
            (AgentSessionOrigin.ChatbotBlock, (Guid?)delegation.DelegationId, (Guid?)Chatbot, (Guid?)Run, (int?)45, agent.PublishedVersionId!.Value),
            (session.Origin, session.DelegationId, session.ChatbotId, session.RunId, session.IdleMinutes, session.AgentVersionId));
        Assert.Empty(world.Outbound.Sends);
        Assert.DoesNotContain(world.Sessions.Turns, turn => turn.Role == AgentTurnRoleKind.Greeting);
        Assert.Null(world.Conversations.StartedAgent);
        Assert.Empty(world.Conversations.Returns);
    }

    // D-B7: firstTurn = lastMessage responde el entrante que llevó la corrida al bloque, con el debounce de siempre (D-A14).
    [Fact]
    public async Task LastMessageAnswersTheInboundThatReachedTheBlock()
    {
        var world = new World();
        var agent = PublishedAgent();
        world.Credentials.Available.Add("anthropic");
        var inbound = Guid.CreateVersion7();
        var delegation = Delegate(world, agent, lastInbound: inbound);

        await world.Inbound().ProcessAsync(DelegatedRow(delegation), Ct);

        var turn = Assert.Single(world.Sessions.Turns);
        Assert.Equal((AgentTurnRoleKind.Customer, (Guid?)inbound, 1), (turn.Role, turn.MessageId, turn.Seq));
        var job = Assert.Single(world.Jobs.Added);
        Assert.Equal((1, Now + AiSessionTimings.Default.Debounce), (job.UpToSeq, job.NextAttemptAt));
    }

    [Fact]
    public async Task NextMessageWaitsForTheCustomer()
    {
        var world = new World();
        var agent = PublishedAgent();
        world.Credentials.Available.Add("anthropic");
        var delegation = Delegate(world, agent, lastInbound: Guid.CreateVersion7(), brief: Brief(firstTurn: "nextMessage"));

        Assert.Equal(AiInboxOutcomes.Started, await world.Inbound().ProcessAsync(DelegatedRow(delegation), Ct));

        Assert.Single(world.Sessions.All);
        Assert.Empty(world.Sessions.Turns);
        Assert.Empty(world.Jobs.Added);
    }

    // D-B7: sin entrante (arranque manual), lastMessage se comporta como nextMessage.
    [Fact]
    public async Task WithoutAnInboundLastMessageWaits()
    {
        var world = new World();
        var agent = PublishedAgent();
        world.Credentials.Available.Add("anthropic");
        var delegation = Delegate(world, agent, lastInbound: null);

        Assert.Equal(AiInboxOutcomes.Started, await world.Inbound().ProcessAsync(DelegatedRow(delegation), Ct));

        Assert.Single(world.Sessions.All);
        Assert.Empty(world.Sessions.Turns);
        Assert.Empty(world.Jobs.Added);
    }

    // D-B18 y §6.6: un agente pausado no abre delegaciones nuevas; la conversación vuelve al flujo por error, sin sesión.
    [Fact]
    public async Task APausedAgentReturnsThroughError()
    {
        var world = new World();
        var agent = PublishedAgent();
        agent.Pause(Now);
        world.Credentials.Available.Add("anthropic");
        var delegation = Delegate(world, agent, lastInbound: Guid.CreateVersion7());

        var outcome = await world.Inbound().ProcessAsync(DelegatedRow(delegation), Ct);

        Assert.Equal(AiInboxOutcomes.Returned, outcome);
        Assert.Empty(world.Sessions.All);
        AssertReturned(world, delegation, "error", "agent_unavailable");
    }

    // C-19: la cuenta que importa es la del proveedor del agente.
    [Fact]
    public async Task WithoutTheProviderAccountItReturnsThroughError()
    {
        var world = new World();
        var agent = PublishedAgent(provider: "openai");
        world.Credentials.Available.Add("anthropic");
        var delegation = Delegate(world, agent, lastInbound: Guid.CreateVersion7());

        Assert.Equal(AiInboxOutcomes.Returned, await world.Inbound().ProcessAsync(DelegatedRow(delegation), Ct));

        Assert.Empty(world.Sessions.All);
        AssertReturned(world, delegation, "error", AiErrorCodes.ProviderNotConfigured);
    }

    // D-B16: con el módulo ai apagado el bloque sale por error, no a la bandeja.
    [Fact]
    public async Task WithTheModuleOffItReturnsThroughError()
    {
        var world = new World();
        var agent = PublishedAgent();
        world.Credentials.Available.Add("anthropic");
        world.Modules.Enabled = false;
        var delegation = Delegate(world, agent, lastInbound: Guid.CreateVersion7());

        Assert.Equal(AiInboxOutcomes.Returned, await world.Inbound().ProcessAsync(DelegatedRow(delegation), Ct));

        Assert.Empty(world.Sessions.All);
        AssertReturned(world, delegation, "error", "module_disabled");
        Assert.Empty(world.Conversations.Handoffs);
    }

    // §6.2 paso 1: una persona tomó la conversación antes de que la fila llegara (la delegación quedó Interrupted).
    [Fact]
    public async Task ADelegationNoLongerLiveIsSkipped()
    {
        var world = new World();
        var agent = PublishedAgent();
        world.Credentials.Available.Add("anthropic");
        var delegation = Delegate(world, agent, lastInbound: Guid.CreateVersion7(), status: "Interrupted");

        Assert.Equal(AiInboxOutcomes.Skipped, await world.Inbound().ProcessAsync(DelegatedRow(delegation), Ct));

        Assert.Empty(world.Sessions.All);
        Assert.Empty(world.Conversations.Returns);
    }

    // Un entrante que el procesador toma antes que la fila Delegated (un reintento por candado) abre la misma sesión del bloque, con el
    // entrante del bloque y el nuevo; la fila Delegated después es un duplicado. Nunca abre una sesión de conexión con saludo.
    [Fact]
    public async Task AnInboundBeforeTheDelegatedRowOpensTheSameBlockSession()
    {
        var world = new World();
        var agent = PublishedAgent();
        world.Credentials.Available.Add("anthropic");
        var reached = Guid.CreateVersion7();
        var delegation = Delegate(world, agent, lastInbound: reached);
        var inbound = InboundRow();

        Assert.Equal(AiInboxOutcomes.Started, await world.Inbound().ProcessAsync(inbound, Ct));
        Assert.Equal(AiInboxOutcomes.Duplicate, await world.Inbound().ProcessAsync(DelegatedRow(delegation), Ct));

        var session = Assert.Single(world.Sessions.All);
        Assert.Equal((AiDeterministicIds.SessionForDelegation(delegation.DelegationId), true), (session.Id, session.IsBlock));
        Assert.Equal([reached, inbound.MessageId], world.Sessions.Turns.Select(turn => turn.MessageId!.Value));
        Assert.Empty(world.Outbound.Sends);
        Assert.Single(world.Jobs.Added);
    }

    // ---- §6.3: fin de la sesión del bloque, una prueba por fila de la tabla ----

    /// <summary>Una sesión viva del bloque sobre su delegación viva, con la conversación en manos del agente.</summary>
    private static (AgentSession Session, AiDelegation Delegation) LiveBlock(
        World world, Agent agent, int? idleMinutes = null, DateTimeOffset? updatedAt = null, bool windowOpen = true)
    {
        var delegation = Delegate(world, agent, lastInbound: Guid.CreateVersion7());
        world.Conversations.State = DelegatedState(agent.Id, delegation.DelegationId, windowOpen);
        var session = world.Sessions.LiveBlock(agent, delegation.DelegationId, idleMinutes, updatedAt);
        return (session, delegation);
    }

    private static Agent AgentWithHandoffMessage(int maxReplies = 20)
    {
        var agent = PublishedAgentWith(config => config with
        {
            Handoff = config.Handoff with { Message = "Te paso con una persona.", MaxRepliesWithoutHuman = maxReplies },
        });
        return agent;
    }

    private static Agent PublishedAgentWith(Func<AgentConfig, AgentConfig> change)
    {
        var config = change(AgentConfig.Default(new AgentModelRef("anthropic", "claude-opus-5-5")) with { Instructions = "Responde dudas." });
        var agent = Agent.Create(Tenant, "Asesor virtual", config, By, Now);
        agent.Publish([], By, Now);
        return agent;
    }

    private static ModelJob HeldJob(AgentSession session, string resultJson)
    {
        var job = ModelJob.ForSession(Guid.CreateVersion7(), Tenant, session.Id, session.NextSeq - 1, Now, Now);
        job.HoldResult(resultJson, Now);
        return job;
    }

    [Fact]
    public async Task FinishObjectiveReturnsDone()
    {
        var world = new World();
        var (session, delegation) = LiveBlock(world, AgentWithHandoffMessage());
        var job = HeldJob(session, """{"type":"finished","text":"El cliente quiere 3 jabones."}""");

        await world.Worker().ProcessAsync(job, Ct);

        AssertReturned(world, delegation, "done", "objective_completed", summary: "El cliente quiere 3 jabones.");
        Assert.Equal((AgentSessionStatus.Returned, "done", "objective_completed"), (session.Status, session.EndReason, session.FailureCode));
        Assert.Equal("El cliente quiere 3 jabones.", session.ObjectiveSummary);
        Assert.Equal(ModelJobStatus.Done, job.Status);
        Assert.Empty(world.Outbound.Sends);
        Assert.Empty(world.Conversations.Handoffs);
    }

    // D-B9: handoff_to_human dentro del bloque no traspasa ni manda el mensaje de traspaso: sale.
    [Theory]
    [InlineData("customer_asked", "human")]
    [InlineData("complaint", "human")]
    public async Task CustomerAskedReturnsHuman(string reason, string output)
    {
        var world = new World();
        var (session, delegation) = LiveBlock(world, AgentWithHandoffMessage());

        await world.Worker().ProcessAsync(HeldJob(session, $$"""{"type":"handed_off","reason":"{{reason}}"}"""), Ct);

        AssertReturned(world, delegation, output, reason);
        Assert.Equal(AgentSessionStatus.Returned, session.Status);
        Assert.Empty(world.Outbound.Sends);
        Assert.Empty(world.Conversations.Handoffs);
    }

    [Theory]
    [InlineData("no_answer")]
    [InlineData("out_of_scope")]
    [InlineData("quotation_customer_not_ready")]
    public async Task NoAnswerReturnsNoAnswer(string reason)
    {
        var world = new World();
        var (session, delegation) = LiveBlock(world, AgentWithHandoffMessage());

        await world.Worker().ProcessAsync(HeldJob(session, $$"""{"type":"handed_off","reason":"{{reason}}"}"""), Ct);

        AssertReturned(world, delegation, "noAnswer", reason);
        Assert.Empty(world.Conversations.Handoffs);
    }

    // D-B13 y P-4b: el resultado lleva el número y la asesora, para que el motor asigne si human no está conectada; queda auditado.
    [Fact]
    public async Task AQuotationDraftReturnsHumanWithTheNumberAndAdvisor()
    {
        var world = new World();
        var agent = AgentWithHandoffMessage();
        var (session, delegation) = LiveBlock(world, agent);
        var advisor = Guid.CreateVersion7();
        world.Sessions.Turns.Add(session.AppendTurn(AgentTurnRoleKind.Customer, Guid.CreateVersion7(), null, [], false, Now));

        await world.Worker().ProcessAsync(
            HeldJob(session, $$"""{"type":"handed_off","reason":"quotation_draft_created","memberId":"{{advisor}}","quotationNumber":"COT-0042"}"""), Ct);

        var root = AssertReturned(world, delegation, "human", "quotation_draft_created");
        Assert.Equal("COT-0042", root.GetProperty("quotationNumber").GetString());
        Assert.Equal(advisor, root.GetProperty("advisorMemberId").GetGuid());
        Assert.Equal((Tenant, AiAuditActions.QuotationDraftCreated, agent.Id), Assert.Single(world.Audit.Entries));
        Assert.Empty(world.Conversations.Handoffs);
    }

    [Fact]
    public async Task MaxRepliesReturnsHuman()
    {
        var world = new World();
        var (session, delegation) = LiveBlock(world, AgentWithHandoffMessage(maxReplies: 1));

        await world.Worker().ProcessAsync(HeldJob(session, """{"type":"replied","text":"Cuesta 12.000."}"""), Ct);

        Assert.Equal(["Cuesta 12.000."], world.Outbound.Sends.Select(send => send.Text));
        AssertReturned(world, delegation, "human", AgentHandoffCodes.MaxRepliesWithoutHuman);
        Assert.Empty(world.Conversations.Handoffs);
    }

    [Theory]
    [InlineData("""{"type":"failed","kind":1,"failureReason":0,"code":"ai.provider.quota_exhausted"}""", "ai.provider.quota_exhausted")]
    [InlineData("""{"type":"retry","code":"ai.provider.unavailable"}""", "ai.provider.unavailable")]
    [InlineData("""{"type":"failed","kind":0,"failureReason":2,"code":null}""", "model_calls_exceeded")]
    public async Task ProviderErrorsReturnError(string held, string reason)
    {
        var world = new World();
        var (session, delegation) = LiveBlock(world, AgentWithHandoffMessage());

        await world.Worker().ProcessAsync(HeldJob(session, held), Ct);

        AssertReturned(world, delegation, "error", reason);
        Assert.Equal(AgentSessionStatus.Returned, session.Status);
        Assert.Empty(world.Outbound.Sends);
        Assert.Empty(world.Conversations.Handoffs);
    }

    [Fact]
    public async Task SendFailedReturnsError()
    {
        var world = new World();
        var (session, delegation) = LiveBlock(world, AgentWithHandoffMessage());
        world.Outbound.NextResult = new AiSendResult(AiSendStatus.Rejected, Code: "messaging.message.rejected");

        await world.Worker().ProcessAsync(HeldJob(session, """{"type":"replied","text":"Hola."}"""), Ct);

        AssertReturned(world, delegation, "error", AgentHandoffCodes.SendFailed);
        Assert.Empty(world.Conversations.Handoffs);
    }

    // §6.4: con la ventana cerrada no hay a quién mandarle nada: vuelve sin salida y el motor termina la corrida WindowClosed.
    [Fact]
    public async Task AWindowClosedReturnsWithoutOutput()
    {
        var world = new World();
        var (session, delegation) = LiveBlock(world, AgentWithHandoffMessage(), windowOpen: false);

        await world.Worker().ProcessAsync(HeldJob(session, """{"type":"replied","text":"Hola."}"""), Ct);

        AssertReturned(world, delegation, null, AgentBlockReasons.WindowClosed);
        Assert.Equal(AgentSessionStatus.WindowClosed, session.Status);
        Assert.Empty(world.Outbound.Sends);
        Assert.Empty(world.Conversations.Handoffs);
    }

    // §6.4: un contacto bloqueado vuelve sin salida; la sesión termina Interrupted como hoy.
    [Fact]
    public async Task ABlockedContactReturnsWithoutOutput()
    {
        var world = new World();
        var (session, delegation) = LiveBlock(world, AgentWithHandoffMessage());
        world.Outbound.NextResult = new AiSendResult(AiSendStatus.Blocked);

        await world.Worker().ProcessAsync(HeldJob(session, """{"type":"replied","text":"Hola."}"""), Ct);

        AssertReturned(world, delegation, null, AgentBlockReasons.Blocked);
        Assert.Equal(AgentSessionStatus.Interrupted, session.Status);
    }

    // Review Focus 4: ninguna terminación de una sesión de bloque traspasa por su cuenta; tampoco la del módulo apagado, la del agente borrado
    // o la del trabajo que se reclamó demasiadas veces.
    [Fact]
    public async Task ABlockSessionNeverHandsOffByItself()
    {
        var off = new World();
        var (offSession, offDelegation) = LiveBlock(off, AgentWithHandoffMessage());
        off.Modules.Enabled = false;
        await off.Worker().ProcessAsync(HeldJob(offSession, """{"type":"replied","text":"Hola."}"""), Ct);

        var deleted = new World();
        var agent = AgentWithHandoffMessage();
        var (deletedSession, deletedDelegation) = LiveBlock(deleted, agent);
        deleted.Sessions.Forget(agent);
        await deleted.Worker().ProcessAsync(ModelJob.ForSession(Guid.CreateVersion7(), Tenant, deletedSession.Id, 1, Now, Now), Ct);

        var claimed = new World();
        var (claimedSession, claimedDelegation) = LiveBlock(claimed, AgentWithHandoffMessage());
        var job = ModelJob.ForSession(Guid.CreateVersion7(), Tenant, claimedSession.Id, 1, Now, Now);
        typeof(ModelJob).GetProperty(nameof(ModelJob.Attempts))!.SetValue(job, ModelJob.MaxClaims + 1);
        await claimed.Worker().ProcessAsync(job, Ct);

        AssertReturned(off, offDelegation, "error", AgentBlockReasons.ModuleDisabled);
        AssertReturned(deleted, deletedDelegation, "error", AgentBlockReasons.AgentUnavailable);
        AssertReturned(claimed, claimedDelegation, "error", AgentHandoffCodes.JobTimedOut);
        Assert.All([off, deleted, claimed], world => Assert.Empty(world.Conversations.Handoffs));
        Assert.All([offSession, deletedSession, claimedSession], session => Assert.Equal(AgentSessionStatus.Returned, session.Status));
    }

    // Una persona tomó la conversación entre la verificación y el retorno: Mensajería rechaza el retorno y la sesión queda Interrupted.
    [Fact]
    public async Task AReturnTheConversationNoLongerWaitsForInterruptsTheSession()
    {
        var world = new World();
        var (session, delegation) = LiveBlock(world, AgentWithHandoffMessage());
        world.Conversations.Delegations[delegation.DelegationId] = delegation with { Status = "Interrupted" };

        await world.Worker().ProcessAsync(HeldJob(session, """{"type":"finished","text":"Listo."}"""), Ct);

        Assert.Empty(world.Conversations.Returns);
        Assert.Equal((AgentSessionStatus.Interrupted, AgentSessionEndReasons.HumanTookOver), (session.Status, session.EndReason));
    }

    // D-B11 y §6.5: la inactividad es la del bloque, no la de 24 h; vuelve por idle. Una sesión de conexión con el mismo silencio sigue viva.
    [Fact]
    public async Task ABlockSessionIdlesOutAtItsOwnMinutes()
    {
        var world = new World();
        var (session, delegation) = LiveBlock(world, AgentWithHandoffMessage(), idleMinutes: 5, updatedAt: Now.AddMinutes(-6));
        var quiet = new World();
        var (patient, _) = LiveBlock(quiet, AgentWithHandoffMessage(), idleMinutes: 30, updatedAt: Now.AddMinutes(-6));

        Assert.Equal(1, await world.Sweeper().SweepIdleAsync(Ct));
        Assert.Equal(0, await quiet.Sweeper().SweepIdleAsync(Ct));

        AssertReturned(world, delegation, "idle", AgentBlockReasons.Idle);
        Assert.Equal((AgentSessionStatus.Returned, "idle"), (session.Status, session.EndReason));
        Assert.False(world.Conversations.Ended);
        Assert.True(patient.IsLive);
    }

    // D-B16: apagar ai con una delegación viva la devuelve al flujo por error, no a la bandeja.
    [Fact]
    public async Task WithTheModuleOffTheSweepReturnsTheBlockThroughError()
    {
        var world = new World();
        var (session, delegation) = LiveBlock(world, AgentWithHandoffMessage());
        world.Modules.Enabled = false;

        Assert.Equal(1, await world.Sweeper().SweepDisabledModuleAsync(Ct));

        AssertReturned(world, delegation, "error", AgentBlockReasons.ModuleDisabled);
        Assert.Equal(AgentSessionStatus.Returned, session.Status);
        Assert.Empty(world.Conversations.Handoffs);
    }

    // §6.5: las delegaciones vivas que su conversación ya no apunta las interrumpe el barrido de Ai, junto con el de inactividad.
    [Fact]
    public async Task AnOrphanDelegationIsInterrupted()
    {
        var world = new World();
        world.DelegationSweep.Orphans = 2;

        Assert.Equal(2, await world.Sweeper().SweepOrphanDelegationsAsync(Ct));

        Assert.Equal([Now], world.DelegationSweep.Swept);
    }

    private static JsonElement AssertReturned(World world, AiDelegation delegation, string? output, string reason, string? summary = null)
    {
        var (delegationId, json) = Assert.Single(world.Conversations.Returns);
        Assert.Equal(delegation.DelegationId, delegationId);
        var root = JsonDocument.Parse(json).RootElement;
        Assert.Equal(output, root.GetProperty("output").GetString());
        Assert.Equal(reason, root.GetProperty("reason").GetString());
        if (summary is not null)
        {
            Assert.Equal(summary, root.GetProperty("summary").GetString());
        }

        return root;
    }

    /// <summary><see cref="IAiConversations"/> con delegaciones: el retorno exige la delegación viva de la conversación, como Mensajería.</summary>
    internal sealed class BlockConversations : IAiConversations
    {
        public AiConversationState? State { get; set; }

        public Dictionary<Guid, AiDelegation> Delegations { get; } = [];

        public List<(Guid DelegationId, string ResultJson)> Returns { get; } = [];

        public List<(AgentHandoffCause Cause, Guid? AssignTo)> Handoffs { get; } = [];

        public Guid? StartedAgent { get; private set; }

        public bool Ended { get; private set; }

        public Task<AiConversationState?> GetAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) => Task.FromResult(State);

        public Task StartAsync(Guid tenantId, Guid conversationId, Guid agentId, CancellationToken cancellationToken)
        {
            StartedAgent = agentId;
            return Task.CompletedTask;
        }

        public Task HandoffAsync(Guid tenantId, Guid conversationId, AgentHandoffCause cause, Guid? assignToMemberId, CancellationToken cancellationToken)
        {
            Handoffs.Add((cause, assignToMemberId));
            return Task.CompletedTask;
        }

        public Task EndAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken)
        {
            Ended = true;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<Guid, AiThreadMessage>> ReadMessagesAsync(
            Guid tenantId, IReadOnlyCollection<Guid> messageIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, AiThreadMessage>>(
                messageIds.ToDictionary(id => id, id => new AiThreadMessage(id, AgentTurnKind.Text, "¿Cuánto cuesta?")));

        public Task<AiDelegation?> ReadDelegationAsync(Guid tenantId, Guid delegationId, CancellationToken cancellationToken) =>
            Task.FromResult(Delegations.GetValueOrDefault(delegationId));

        public Task ReturnToChatbotAsync(Guid tenantId, Guid conversationId, Guid delegationId, string resultJson, CancellationToken cancellationToken)
        {
            if (State?.AutomationDelegationId != delegationId || !Delegations.TryGetValue(delegationId, out var delegation) || !delegation.IsLive)
            {
                throw new TestDomainException("messaging.automation.delegation_not_live");
            }

            Returns.Add((delegationId, resultJson));
            Delegations[delegationId] = delegation with { Status = "Returned" };
            State = State with { AutomationBotKind = "Chatbot", AutomationAgentId = null, AutomationDelegationId = null };
            return Task.CompletedTask;
        }
    }

    internal sealed class TestDomainException(string code) : DomainException(code, code);
}
