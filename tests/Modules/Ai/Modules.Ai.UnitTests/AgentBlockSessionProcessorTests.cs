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

        public AiInboxProcessor Inbound() => new(
            UnitOfWork, Sessions, Jobs, Inbox, Conversations, new FakeHours(), Modules, Credentials, Outbound, AiSessionTimings.Default, Clock,
            NullLogger<AiInboxProcessor>.Instance);
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

    private static void AssertReturned(World world, AiDelegation delegation, string? output, string reason)
    {
        var (delegationId, json) = Assert.Single(world.Conversations.Returns);
        Assert.Equal(delegation.DelegationId, delegationId);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(output, root.GetProperty("output").GetString());
        Assert.Equal(reason, root.GetProperty("reason").GetString());
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
