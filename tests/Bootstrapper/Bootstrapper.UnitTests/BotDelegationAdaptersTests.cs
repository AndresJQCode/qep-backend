using System.Reflection;
using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Modules.Automations.Application;
using Modules.Automations.Application.Engine;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>Spec 2026-11-17 (agente IA en chatbots) §5.3, §6.1 y §6.3: los adaptadores de la delegación escriben la delegación, la
/// conversación y la fila de la cola en <b>un</b> guardado de Mensajería, con las reglas del agregado. Lo de la base (índice, CHECK) se prueba
/// en <c>BotDelegationPersistenceTests</c>.</summary>
public sealed class BotDelegationAdaptersTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid ChatbotId = Guid.CreateVersion7();
    private static readonly Guid AgentId = Guid.CreateVersion7();
    private const string Brief = """{"objective":"Resuelve dudas","context":"","firstTurn":"lastMessage","idleMinutes":60}""";
    private const string Result = """{"output":"done","reason":"objective_completed","summary":"Listo"}""";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Conversation ChatbotConversation()
    {
        var conversation = Conversation.Start(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "573001234567", "Laura", Now);
        conversation.StartAutomation(ChatbotId, Now);
        return conversation;
    }

    [Fact]
    public async Task DelegatingWritesTheDelegationTheConversationAndTheDelegatedRowInOneSave()
    {
        var conversation = ChatbotConversation();
        var world = new World(conversation);
        var inbound = Guid.CreateVersion7();
        var runId = Guid.CreateVersion7();

        var delegationId = await world.Automations().DelegateToAgentAsync(
            conversation.TenantId, conversation.Id, new DelegationRequest(runId, ChatbotId, "ai-agent-1", AgentId, inbound, Brief), Ct);

        Assert.Equal(delegationId, conversation.AutomationDelegationId);
        Assert.Equal(BotKind.Agent, conversation.AutomationBotKind);
        var delegation = Assert.Single(world.Delegations);
        Assert.Equal(
            (delegationId, conversation.Id, conversation.ConnectionId, ChatbotId, runId, "ai-agent-1", AgentId, (Guid?)inbound, Brief, BotDelegationStatus.Live),
            (delegation.Id, delegation.ConversationId, delegation.ConnectionId, delegation.ChatbotId, delegation.RunId, delegation.NodeId,
                delegation.AgentId, delegation.LastInboundMessageId, delegation.Brief, delegation.Status));
        Assert.Equal([$"{AutomationInboxKinds.Delegated} {delegationId}"], world.Queued);
        Assert.Equal(
            new ConversationEvent(ConversationEventType.AutomationDelegated, ChatbotId: ChatbotId, ChatbotName: "Ventas", AgentId: AgentId, AgentName: "Asesor IA"),
            Assert.Single(world.Events));
        Assert.Equal([MessagingAuditActions.AutomationDelegated], world.Audits);
        Assert.Equal(1, world.Saves);
    }

    // Revisión de la rama, hallazgo 1: un entrante que llegó mientras la unidad del chatbot corría quedó en la cola del chatbot. En la
    // transacción que delega pasa a la del agente (menos la fila que la unidad procesa), y como es más viejo que la fila Delegated, el
    // agente lo atiende primero y abre la sesión con él.
    [Fact]
    public async Task DelegatingMovesTheOtherPendingRowsToTheAgentQueueInTheSameTransaction()
    {
        var conversation = ChatbotConversation();
        var world = new World(conversation);
        var row = Guid.CreateVersion7();

        await world.Automations().DelegateToAgentAsync(
            conversation.TenantId, conversation.Id,
            new DelegationRequest(Guid.CreateVersion7(), ChatbotId, "ai-agent-1", AgentId, row, Brief, InboxRowId: row), Ct);

        Assert.Equal(["begin", "interrupt live", $"reassign {BotKind.Agent} except {row}", "save", "commit"], world.Log);
    }

    // Hallazgo 1: el traspaso, la resolución y el fin del chatbot sobre una conversación delegada sacan al agente como lo hace una persona:
    // la delegación queda Interrupted y los dos bots reciben su señal (ReleaseRunSignal). Antes sólo se limpiaba la columna.
    [Theory]
    [InlineData("handoff")]
    [InlineData("resolve")]
    [InlineData("end")]
    public async Task LeavingADelegatedConversationInterruptsTheDelegationAndSignalsBothBots(string change)
    {
        var conversation = ChatbotConversation();
        var world = new World(conversation);
        await world.Automations().DelegateToAgentAsync(
            conversation.TenantId, conversation.Id, new DelegationRequest(Guid.CreateVersion7(), ChatbotId, "ai-agent-1", AgentId, null, Brief), Ct);
        world.Reset();

        await ChangeAsync(world.Automations(), conversation, change);

        Assert.Equal(BotDelegationStatus.Interrupted, Assert.Single(world.Delegations).Status);
        Assert.Equal([$"{AutomationInboxKinds.ReleaseRun} {BotKind.Agent}", $"{AutomationInboxKinds.ReleaseRun} {BotKind.Chatbot}"], world.Queued);
        Assert.Null(conversation.AutomationDelegationId);
        Assert.Equal(1, world.Saves);
    }

    // D-6 de 2026-11-03: sin delegación el bot no se suelta a sí mismo; nada cambia ahí.
    [Theory]
    [InlineData("handoff")]
    [InlineData("resolve")]
    [InlineData("end")]
    public async Task LeavingAConversationWithoutDelegationSignalsNobody(string change)
    {
        var conversation = ChatbotConversation();
        var world = new World(conversation);

        await ChangeAsync(world.Automations(), conversation, change);

        Assert.Empty(world.Queued);
        Assert.Equal(1, world.Saves);
    }

    private static Task ChangeAsync(AutomationConversations automations, Conversation conversation, string change) => change switch
    {
        "handoff" => automations.HandoffAsync(
            conversation.TenantId, conversation.Id, HandoffCause.FlowFailed(AutomationsRunFailures.ManualStartFailed, null, null), null, Ct),
        "resolve" => automations.ResolveAsync(conversation.TenantId, conversation.Id, ChatbotId, null, Ct),
        _ => automations.EndAsync(conversation.TenantId, conversation.Id, Ct),
    };

    // Revisión de la rama, hallazgo 3: una delegación Live que quedó colgada de la conversación (un traspaso que limpió la columna sin
    // cerrarla) frenaba toda delegación nueva por IX_bot_delegations_live. Delegar la cierra en la misma transacción, antes de agregar la nueva.
    [Fact]
    public async Task DelegatingInterruptsAnOrphanLiveDelegationOfTheConversationFirst()
    {
        var conversation = ChatbotConversation();
        var world = new World(conversation);
        var orphan = BotDelegation.Start(
            Guid.CreateVersion7(), conversation.TenantId, conversation.Id, conversation.ConnectionId, ChatbotId, Guid.CreateVersion7(), "ai-agent-0",
            AgentId, null, Brief, Now.AddHours(-1));
        world.Delegations.Add(orphan);

        var delegationId = await world.Automations().DelegateToAgentAsync(
            conversation.TenantId, conversation.Id, new DelegationRequest(Guid.CreateVersion7(), ChatbotId, "ai-agent-1", AgentId, null, Brief), Ct);

        Assert.Equal(BotDelegationStatus.Interrupted, orphan.Status);
        Assert.Equal(BotDelegationStatus.Live, world.Delegations.Single(value => value.Id == delegationId).Status);
        Assert.Equal(["begin", "interrupt live", $"reassign {BotKind.Agent} except none", "save", "commit"], world.Log);
    }

    // Hallazgo 2: la fila Delegated de una delegación que la conversación ya no apunta. Mensajería la cierra y le avisa a la corrida que
    // espera en el bloque; si la conversación la sigue apuntando, no toca nada.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnOrphanDelegationIsInterruptedAndItsRunSignaled(bool orphan)
    {
        var conversation = ChatbotConversation();
        var world = new World(conversation);
        var delegationId = await world.Automations().DelegateToAgentAsync(
            conversation.TenantId, conversation.Id, new DelegationRequest(Guid.CreateVersion7(), ChatbotId, "ai-agent-1", AgentId, null, Brief), Ct);
        if (orphan)
        {
            conversation.Take(Guid.CreateVersion7(), Now.AddMinutes(1));
        }

        world.Reset();

        await world.Ai().InterruptOrphanDelegationAsync(conversation.TenantId, conversation.Id, delegationId, Ct);

        Assert.Equal(orphan ? BotDelegationStatus.Interrupted : BotDelegationStatus.Live, Assert.Single(world.Delegations).Status);
        Assert.Equal(orphan ? [$"{AutomationInboxKinds.ReleaseRun} {BotKind.Chatbot}"] : [], world.Queued);
        Assert.Equal(orphan ? 1 : 0, world.Saves);
    }

    [Fact]
    public async Task DelegatingAConversationAPersonTookThrowsTheDomainError()
    {
        var conversation = ChatbotConversation();
        conversation.Take(Guid.CreateVersion7(), Now);
        var world = new World(conversation);

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => world.Automations().DelegateToAgentAsync(
            conversation.TenantId, conversation.Id, new DelegationRequest(Guid.CreateVersion7(), ChatbotId, "ai-agent-1", AgentId, null, Brief), Ct));

        Assert.Equal(MessagingErrorCodes.AutomationNotAvailable, error.Code);
        Assert.Empty(world.Delegations);
        Assert.Empty(world.Queued);
        Assert.Equal(0, world.Saves);
    }

    [Fact]
    public async Task ReturningWritesTheResultReturnsTheChatbotAndQueuesAgentReturnedTogether()
    {
        var conversation = ChatbotConversation();
        var world = new World(conversation);
        var delegationId = await world.Automations().DelegateToAgentAsync(
            conversation.TenantId, conversation.Id, new DelegationRequest(Guid.CreateVersion7(), ChatbotId, "ai-agent-1", AgentId, null, Brief), Ct);
        world.Reset();

        await world.Ai().ReturnToChatbotAsync(conversation.TenantId, conversation.Id, delegationId, Result, Ct);

        var delegation = Assert.Single(world.Delegations);
        Assert.Equal(BotDelegationStatus.Returned, delegation.Status);
        Assert.Equal(Result, delegation.Result);
        Assert.Equal(BotKind.Chatbot, conversation.AutomationBotKind);
        Assert.Equal(ChatbotId, conversation.AutomationChatbotId);
        Assert.Null(conversation.AutomationDelegationId);
        Assert.Equal([$"{AutomationInboxKinds.AgentReturned} {delegationId}"], world.Queued);
        Assert.Equal(
            new ConversationEvent(
                ConversationEventType.AutomationReturned, ChatbotId: ChatbotId, ChatbotName: "Ventas", AgentId: AgentId, AgentName: "Asesor IA",
                Output: "done"),
            Assert.Single(world.Events));
        Assert.Equal([MessagingAuditActions.AutomationReturned], world.Audits);
        Assert.Equal(1, world.Saves);
    }

    // Revisión de la rama, hallazgo 6 (el espejo del 1): un entrante que la ingesta encoló para el agente antes de que el retorno se
    // confirmara pasa a la cola del chatbot en la misma transacción. La fila AgentReturned va delante de él: la corrida tiene que salir del
    // bloque antes de leerlo como respuesta (con la corrida todavía delegada, el motor lo saltaría).
    [Fact]
    public async Task ReturningMovesThePendingRowsToTheChatbotBehindTheAgentReturnedRow()
    {
        var conversation = ChatbotConversation();
        var world = new World(conversation);
        var delegationId = await world.Automations().DelegateToAgentAsync(
            conversation.TenantId, conversation.Id, new DelegationRequest(Guid.CreateVersion7(), ChatbotId, "ai-agent-1", AgentId, null, Brief), Ct);
        world.Reset();
        var burst = Now.AddSeconds(-30);
        world.OldestReassigned = burst;

        await world.Ai().ReturnToChatbotAsync(conversation.TenantId, conversation.Id, delegationId, Result, Ct);

        Assert.Equal(["begin", $"reassign {BotKind.Chatbot} except none", "save", "commit"], world.Log);
        Assert.Equal([$"{AutomationInboxKinds.AgentReturned} {delegationId}"], world.Queued);
        Assert.True(Assert.Single(world.QueuedAt) < burst);
    }

    [Fact]
    public async Task WithNothingMovedTheAgentReturnedRowIsQueuedNow()
    {
        var conversation = ChatbotConversation();
        var world = new World(conversation);
        var delegationId = await world.Automations().DelegateToAgentAsync(
            conversation.TenantId, conversation.Id, new DelegationRequest(Guid.CreateVersion7(), ChatbotId, "ai-agent-1", AgentId, null, Brief), Ct);
        world.Reset();

        await world.Ai().ReturnToChatbotAsync(conversation.TenantId, conversation.Id, delegationId, Result, Ct);

        Assert.Equal(Now, Assert.Single(world.QueuedAt));
    }

    [Fact]
    public async Task ReturningADelegationAPersonInterruptedThrowsAndWritesNothing()
    {
        var conversation = ChatbotConversation();
        var world = new World(conversation);
        var delegationId = await world.Automations().DelegateToAgentAsync(
            conversation.TenantId, conversation.Id, new DelegationRequest(Guid.CreateVersion7(), ChatbotId, "ai-agent-1", AgentId, null, Brief), Ct);
        conversation.Take(Guid.CreateVersion7(), Now.AddMinutes(1));
        world.Delegations[0].Interrupt(Now.AddMinutes(1));
        world.Reset();

        var error = await Assert.ThrowsAsync<MessagingDomainException>(
            () => world.Ai().ReturnToChatbotAsync(conversation.TenantId, conversation.Id, delegationId, Result, Ct));

        Assert.Equal(MessagingErrorCodes.DelegationNotLive, error.Code);
        Assert.Empty(world.Queued);
        Assert.Equal(0, world.Saves);
        Assert.Equal(BotDelegationStatus.Interrupted, world.Delegations[0].Status);
    }

    [Fact]
    public async Task ReturningTheDelegationOfAnotherConversationThrows()
    {
        var conversation = ChatbotConversation();
        var other = Conversation.Start(Guid.CreateVersion7(), conversation.TenantId, conversation.ConnectionId, "573009999999", "Ana", Now);
        var world = new World(conversation, other);
        var delegationId = await world.Automations().DelegateToAgentAsync(
            conversation.TenantId, conversation.Id, new DelegationRequest(Guid.CreateVersion7(), ChatbotId, "ai-agent-1", AgentId, null, Brief), Ct);
        world.Reset();

        var error = await Assert.ThrowsAsync<MessagingDomainException>(
            () => world.Ai().ReturnToChatbotAsync(conversation.TenantId, other.Id, delegationId, Result, Ct));

        Assert.Equal(MessagingErrorCodes.DelegationNotLive, error.Code);

        Assert.Equal(BotDelegationStatus.Live, world.Delegations[0].Status);
        Assert.Equal(0, world.Saves);
    }

    [Fact]
    public async Task BothSidesReadTheDelegationAsItIs()
    {
        var conversation = ChatbotConversation();
        var world = new World(conversation);
        var inbound = Guid.CreateVersion7();
        var runId = Guid.CreateVersion7();
        var delegationId = await world.Automations().DelegateToAgentAsync(
            conversation.TenantId, conversation.Id, new DelegationRequest(runId, ChatbotId, "ai-agent-1", AgentId, inbound, Brief), Ct);

        var forAi = await world.Ai().ReadDelegationAsync(conversation.TenantId, delegationId, Ct);
        Assert.NotNull(forAi);
        Assert.Equal((conversation.Id, conversation.ConnectionId, ChatbotId, runId, AgentId, (Guid?)inbound, Brief, "Live"),
            (forAi.ConversationId, forAi.ConnectionId, forAi.ChatbotId, forAi.RunId, forAi.AgentId, forAi.LastInboundMessageId, forAi.BriefJson, forAi.Status));
        Assert.True(forAi.IsLive);

        await world.Ai().ReturnToChatbotAsync(conversation.TenantId, conversation.Id, delegationId, Result, Ct);
        var forAutomations = await world.Automations().ReadDelegationResultAsync(conversation.TenantId, delegationId, Ct);

        Assert.NotNull(forAutomations);
        Assert.Equal((delegationId, conversation.Id, ChatbotId, runId, "ai-agent-1", "Returned", (string?)Result),
            (forAutomations.DelegationId, forAutomations.ConversationId, forAutomations.ChatbotId, forAutomations.RunId, forAutomations.NodeId,
                forAutomations.Status, forAutomations.ResultJson));
        Assert.Null(await world.Automations().ReadDelegationResultAsync(Guid.CreateVersion7(), delegationId, Ct));
        Assert.Null(await world.Ai().ReadDelegationAsync(conversation.TenantId, Guid.CreateVersion7(), Ct));
    }

    /// <summary>Mensajería en memoria: una conversación, las delegaciones y lo que cada adaptador agregó antes de guardar.</summary>
    private sealed class World
    {
        private readonly Conversation conversation;
        private readonly ServiceProvider provider;

        public World(Conversation conversation, params Conversation[] others)
        {
            this.conversation = conversation;
            Conversation[] all = [conversation, .. others];
            var services = new ServiceCollection();
            services.AddScoped(_ => Stub<IConversationRepository>((method, args) => method.Name == nameof(IConversationRepository.FindAsync)
                ? Task.FromResult(all.SingleOrDefault(value => value.TenantId == (Guid)args![0]! && value.Id == (Guid)args[1]!))
                : throw new NotSupportedException(method.Name)));
            services.AddScoped(_ => Stub<IAutomationsChatbotNames>((_, args) =>
                Task.FromResult<IReadOnlyDictionary<Guid, string>>(((IReadOnlyCollection<Guid>)args![1]!)
                    .Where(id => id == ChatbotId || id == AgentId)
                    .Distinct()
                    .ToDictionary(id => id, id => id == ChatbotId ? "Ventas" : "Asesor IA"))));
            services.AddScoped<IBotDelegationRepository>(_ => new MemoryDelegations(Delegations, Log));
            services.AddScoped(_ => Stub<IAutomationInbox>((method, args) =>
            {
                if (method.Name is nameof(IAutomationInbox.EnqueueDelegated) or nameof(IAutomationInbox.EnqueueAgentReturned))
                {
                    Queued.Add($"{(method.Name == nameof(IAutomationInbox.EnqueueDelegated) ? AutomationInboxKinds.Delegated : AutomationInboxKinds.AgentReturned)} {args![3]}");
                    QueuedAt.Add((DateTimeOffset)args[4]!);
                    return null;
                }

                if (method.Name == nameof(IAutomationInbox.EnqueueReleaseRun))
                {
                    Queued.Add($"{AutomationInboxKinds.ReleaseRun} {args![3]}");
                    return null;
                }

                if (method.Name == nameof(IAutomationInbox.ReassignPendingEngineAsync))
                {
                    Log.Add($"reassign {args![2]} except {args[3]?.ToString() ?? "none"}");
                    return Task.FromResult(OldestReassigned);
                }

                if (method.Name == nameof(IAutomationInbox.FindLastInboundIdAsync))
                {
                    return Task.FromResult<Guid?>(null);
                }

                throw new NotSupportedException(method.Name);
            }));
            services.AddScoped(_ => Stub<IConversationEvents>((_, args) =>
            {
                Events.Add((ConversationEvent)args![3]!);
                return null;
            }));
            services.AddScoped(_ => Stub<IMessagingAuditRecorder>((method, args) =>
            {
                Audits.Add(method.Name == nameof(IMessagingAuditRecorder.RecordBySystem) ? (string)args![1]! : throw new NotSupportedException(method.Name));
                return null;
            }));
            services.AddScoped(_ => Stub<IMessagingUnitOfWork>((method, _) =>
            {
                if (method.Name == nameof(IMessagingUnitOfWork.BeginTransactionAsync))
                {
                    Log.Add("begin");
                    return Task.FromResult(Stub<IMessagingTransaction>((step, _) =>
                    {
                        if (step.Name == nameof(IMessagingTransaction.CommitAsync))
                        {
                            Log.Add("commit");
                            return Task.CompletedTask;
                        }

                        return ValueTask.CompletedTask;
                    }));
                }

                Saves++;
                Log.Add("save");
                return method.Name == nameof(IMessagingUnitOfWork.SaveChangesAsync) ? Task.FromResult(1) : throw new NotSupportedException(method.Name);
            }));
            services.AddScoped(_ => Stub<IMessagingNotifier>((_, _) => Task.CompletedTask));
            services.AddSingleton<IClock>(new FixedClock(Now));
            provider = services.BuildServiceProvider();
        }

        public List<BotDelegation> Delegations { get; } = [];

        public List<string> Queued { get; } = [];

        /// <summary>El <c>now</c> con que se encoló cada fila <c>Delegated</c> o <c>AgentReturned</c>: el orden de la cola es por esa hora.</summary>
        public List<DateTimeOffset> QueuedAt { get; } = [];

        /// <summary>Hallazgos 1, 3 y 6 de la revisión: lo que pasa en la transacción de Mensajería, en orden.</summary>
        public List<string> Log { get; } = [];

        /// <summary>Lo que responde el puerto al mover filas: la hora de la más vieja que movió, o <c>null</c> si no movió ninguna.</summary>
        public DateTimeOffset? OldestReassigned { get; set; }

        public List<ConversationEvent> Events { get; } = [];

        public List<string> Audits { get; } = [];

        public int Saves { get; private set; }

        public void Reset()
        {
            Queued.Clear();
            QueuedAt.Clear();
            Log.Clear();
            Events.Clear();
            Audits.Clear();
            Saves = 0;
        }

        public AutomationConversations Automations() => new AutomationConversations(
            provider.GetRequiredService<IServiceScopeFactory>(), Unused<IConversationQueries>(), Unused<IConversationRepository>(),
            Unused<IMessagingNotifier>(), NullLogger<AutomationConversations>.Instance);

        public AiConversations Ai() => new AiConversations(
            provider.GetRequiredService<IServiceScopeFactory>(), Unused<IConversationQueries>(), null!, new FixedClock(Now),
            NullLogger<AiConversations>.Instance);

        public override string ToString() => conversation.Id.ToString();
    }

    private sealed class MemoryDelegations(List<BotDelegation> delegations, List<string> log) : IBotDelegationRepository
    {
        public Task<int> InterruptLiveAsync(Guid tenantId, Guid conversationId, DateTimeOffset now, CancellationToken cancellationToken)
        {
            log.Add("interrupt live");
            return Task.FromResult(delegations.Count(value =>
                value.TenantId == tenantId && value.ConversationId == conversationId && value.Interrupt(now)));
        }

        public Task<BotDelegation?> FindAsync(Guid tenantId, Guid delegationId, CancellationToken cancellationToken) =>
            Task.FromResult(delegations.SingleOrDefault(value => value.TenantId == tenantId && value.Id == delegationId));

        public void Add(BotDelegation delegation) => delegations.Add(delegation);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private static T Stub<T>(Func<MethodInfo, object?[]?, object?> handler)
        where T : class
    {
        var proxy = DispatchProxy.Create<T, StubProxy>();
        ((StubProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    private static T Unused<T>()
        where T : class => Stub<T>((method, _) => throw new NotSupportedException($"{typeof(T).Name}.{method.Name}"));

    public class StubProxy : DispatchProxy
    {
        internal Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            return Handler(targetMethod, args);
        }
    }
}
