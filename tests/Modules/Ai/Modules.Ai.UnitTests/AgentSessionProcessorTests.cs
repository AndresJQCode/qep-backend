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
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly Guid Connection = Guid.CreateVersion7();
    private static readonly Guid Conversation = Guid.CreateVersion7();
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

        public AiInboxProcessor Inbound() => new(
            UnitOfWork, Sessions, Jobs, Inbox, Conversations, Hours, Modules, Credentials, Outbound, AiSessionTimings.Default, Clock,
            NullLogger<AiInboxProcessor>.Instance);

        public AgentSessionJobProcessor Worker() => new(
            UnitOfWork, Sessions, Conversations, Outbound, Modules, Credentials, null!, null!, null!, null!,
            new AgentTurnRunner(null!, [], null!, Credentials, null!, Clock, NullLogger<AgentTurnRunner>.Instance), Clock,
            NullLogger<AgentSessionJobProcessor>.Instance);
    }

    private static Agent PublishedAgent(string provider = "anthropic", string? greeting = null, int maxReplies = 20, string trigger = "NewConversation")
    {
        var config = AgentConfig.Default(new AgentModelRef(provider, provider == "anthropic" ? "claude-opus-5-5" : "gpt-6-luna")) with
        {
            Instructions = "Responde dudas.",
            Greeting = greeting,
            Trigger = trigger,
        };
        config = config with { Handoff = config.Handoff with { MaxRepliesWithoutHuman = maxReplies } };
        var agent = Agent.Create(Tenant, "Asesor virtual", config, By, Now);
        agent.SetConnections([Connection], Now);
        agent.Publish([], By, Now);
        return agent;
    }

    private static AiInboxItem Inbound(bool opened = true) =>
        new(Guid.CreateVersion7(), Tenant, Connection, Conversation, opened, Now, 1, ReleaseRun: false);

    private static AiConversationState State(string automation = "None", string? bot = null, Guid? agentId = null, bool windowOpen = true) =>
        new("Open", null, automation, bot, agentId, Connection, "WhatsApp", null, null, false, windowOpen, TimeSpan.FromHours(20), 4096);

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

    [Fact]
    public async Task AnInboundThatDoesNotOpenTheConversationDoesNotStartTheAgent()
    {
        var world = new World();
        world.Sessions.Serving = PublishedAgent();
        world.Conversations.State = State();
        world.Credentials.Available.Add("anthropic");

        var outcome = await world.Inbound().ProcessAsync(Inbound(opened: false), TestContext.Current.CancellationToken);

        Assert.Equal(AiInboxOutcomes.Skipped, outcome);
        Assert.Empty(world.Sessions.All);
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

        var outcome = await world.Inbound().ProcessAsync(Inbound(opened: false), TestContext.Current.CancellationToken);

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
        var item = Inbound(opened: false);
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

        var outcome = await world.Inbound().ProcessAsync(Inbound(opened: false), TestContext.Current.CancellationToken);

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

        var outcome = await world.Inbound().ProcessAsync(Inbound(opened: false), TestContext.Current.CancellationToken);

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
        Assert.Equal(AgentTurnRoleKind.Agent, Assert.Single(world.Sessions.Turns).Role);
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
        var job = HeldJob(session, """{"type":"failed","kind":"ProviderFailed","failureReason":"ProviderError","code":"ai.provider.quota_exhausted"}""");

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

        public Task<IReadOnlyList<AgentSessionRef>> ListIdleAsync(DateTimeOffset cutoff, int batch, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AgentSessionRef>>([]);

        public Task<IReadOnlyList<AgentSessionRef>> ListLiveAsync(Guid tenantId, int batch, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AgentSessionRef>>([]);

        public Task<IReadOnlyList<Guid>> ListTenantsWithLiveSessionsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Guid>>([]);

        public Task<bool> HasLiveSessionsAsync(Guid tenantId, Guid agentId, CancellationToken cancellationToken) =>
            Task.FromResult(All.Any(session => session.AgentId == agentId && session.IsLive));

        public Task<Agent?> FindServingAgentAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) => Task.FromResult(Serving);

        public Task<Agent?> FindAgentAsync(Guid tenantId, Guid agentId, CancellationToken cancellationToken) =>
            Task.FromResult(_agents.Append(Serving).FirstOrDefault(agent => agent?.Id == agentId));
    }

    internal sealed class FakeJobs : IAiModelJobs
    {
        public List<ModelJob> Added { get; } = [];

        public void Add(ModelJob job) => Added.Add(job);

        public Task<ModelJob?> ClaimAsync(DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken) => Task.FromResult<ModelJob?>(null);
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
        public AiConversationState? State { get; set; }

        public Guid? StartedAgent { get; private set; }

        public List<(AgentHandoffCause Cause, Guid? AssignTo)> Handoffs { get; } = [];

        public Task<AiConversationState?> GetAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) => Task.FromResult(State);

        public Task StartAsync(Guid tenantId, Guid conversationId, Guid agentId, CancellationToken cancellationToken)
        {
            StartedAgent = agentId;
            State = State! with { AutomationStatus = "Active", AutomationBotKind = "Agent", AutomationAgentId = agentId };
            return Task.CompletedTask;
        }

        public Task HandoffAsync(Guid tenantId, Guid conversationId, AgentHandoffCause cause, Guid? assignToMemberId, CancellationToken cancellationToken)
        {
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

        public Task<AiSendResult> SendTextAsync(
            Guid tenantId, Guid conversationId, Guid agentId, Guid clientId, string text, CancellationToken cancellationToken)
        {
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
