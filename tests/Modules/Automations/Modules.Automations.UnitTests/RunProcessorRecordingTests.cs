using System.Text.Json.Nodes;
using BuildingBlocks.Application;
using BuildingBlocks.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Modules.Automations.Application;
using Modules.Automations.Application.Engine;
using Modules.Automations.Application.Http;
using Modules.Automations.Domain;
using Modules.Automations.Domain.Engine;

namespace Modules.Automations.UnitTests;

/// <summary>
/// Spec 2026-10-25 D-E5 y D-E14, plan D-P4: cada unidad del procesador deja su fila en <c>run_units</c> en el mismo guardado que la corrida,
/// y los cierres que no hace el motor dejan la suya, sin pasos. Los dobles son propios (no los de <c>RunProcessorTests</c>, que quedan
/// sin tocar): el repositorio de unidades sólo conserva lo agregado si la unidad de trabajo guardó, como el contexto de EF.
/// </summary>
public sealed class RunProcessorRecordingTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ConnectionId = Guid.CreateVersion7();
    private static readonly Guid ConversationId = Guid.CreateVersion7();
    private static readonly Guid ChatbotId = Guid.CreateVersion7();
    private static readonly Guid VersionId = Guid.CreateVersion7();
    private static readonly Guid MemberId = Guid.CreateVersion7();

    private const string CustomerText = "mi-documento-es-12345678";

    /// <summary>Dos envíos (el mensaje y la pregunta) y espera; la respuesta lleva a un mensaje final.</summary>
    private const string Flow = """
        { "schema": 1, "start": { "firstNodeId": "n1" }, "variables": [],
          "nodes": [
            { "id": "n1", "kind": "message", "title": "Hola", "text": "Hola", "outputs": { "next": "q" } },
            { "id": "q", "kind": "openQuestion", "title": "Documento", "text": "¿Cuál es tu documento?", "saveTo": "doc",
              "validation": { "kind": "none", "pattern": null, "keywords": null }, "outputs": { "answered": "n3", "invalid": null } },
            { "id": "n3", "kind": "message", "title": "Gracias", "text": "Gracias", "outputs": { "next": null } },
            { "id": "h", "kind": "http", "title": "Consultar", "connectionId": "11111111-1111-1111-1111-111111111111", "method": "GET", "path": "/status", "mapping": [], "outputs": { "ok": "n3", "error": null } }
          ] }
        """;

    private static InboundQueueItem NewItem() =>
        new(Guid.CreateVersion7(), TenantId, ConnectionId, ConversationId, Opened: true, DateTimeOffset.UtcNow, Attempts: 1);

    private static AutomationConversationState ActiveState(DateTimeOffset? lastInboundAt = null) =>
        new("Open", null, "Active", ChatbotId, ConnectionId, lastInboundAt ?? DateTimeOffset.UtcNow, "Laura", "573001234567");

    private static ConversationRun LiveRun(
        RunStatus status, string nodeId, int stepNumber, DateTimeOffset updatedAt, Guid? versionId = null) =>
        ConversationRun.Start(
            Guid.CreateVersion7(), TenantId, ConversationId, ConnectionId, ChatbotId, versionId ?? VersionId,
            RunState.Initial(RunVariables.Empty()) with { Status = status, CurrentNodeId = nodeId, StepNumber = stepNumber }, updatedAt);

    [Fact]
    public async Task AUnitLeavesOneRowWithTheRealClientIds()
    {
        var bed = new Bed();
        var item = NewItem();

        var outcome = await bed.Processor().ProcessAsync(item, TestContext.Current.CancellationToken);

        var run = Assert.Single(bed.Runs.Added);
        Assert.Equal(RunOutcomes.Started, outcome);
        var row = Assert.Single(bed.Units.Saved);
        Assert.Equal(
            (TenantId, run.Id, 0, RunUnitTriggers.Start, (Guid?)item.MessageId, RunOutcomes.Started, (string?)null, (string?)null),
            (row.TenantId, row.RunId, row.BaseStep, row.Trigger, row.InboundMessageId, row.Outcome, row.ClosedStatus, row.ClosedReason));
        var sends = RunUnitJson.Parse(row.StepsJson).SelectMany(step => step.Sends).Select(send => send.ClientId).ToArray();
        Guid?[] expected = [DeterministicClientId.For(run.Id, 0, 0), DeterministicClientId.For(run.Id, 0, 1)];
        Assert.Equal(expected, sends);
        Assert.Equal(expected, bed.Messenger.Sent.Select(id => (Guid?)id).ToArray());
    }

    [Fact]
    public async Task AReplyLeavesACustomerMessageRowWithItsInput()
    {
        var bed = new Bed(ActiveState());
        var run = LiveRun(RunStatus.Waiting, "q", stepNumber: 2, DateTimeOffset.UtcNow.AddMinutes(-1));
        bed.Runs.Added.Add(run);
        var item = NewItem();
        bed.Inbox.Messages[item.MessageId] = new InboundMessage(item.MessageId, CustomerMessageKinds.Text, CustomerText, null, null, DateTimeOffset.UtcNow);

        await bed.Processor().ProcessAsync(item, TestContext.Current.CancellationToken);

        var row = Assert.Single(bed.Units.Saved);
        Assert.Equal((RunUnitTriggers.CustomerMessage, 2, (Guid?)item.MessageId), (row.Trigger, row.BaseStep, row.InboundMessageId));
        var steps = RunUnitJson.Parse(row.StepsJson);
        Assert.Equal(item.MessageId, steps[0].InputMessageId);
        Assert.All(steps.Skip(1), step => Assert.Null(step.InputMessageId));
    }

    [Fact]
    public async Task APendingExternalEffectIsAResumeRow()
    {
        var bed = new Bed(ActiveState(), http: new OkHttp());
        var run = LiveRun(RunStatus.Running, "h", stepNumber: 4, DateTimeOffset.UtcNow.AddMinutes(-1));
        bed.Runs.Added.Add(run);
        var item = NewItem();

        await bed.Processor().ProcessAsync(item, TestContext.Current.CancellationToken);

        var row = Assert.Single(bed.Units.Saved);
        Assert.Equal((RunUnitTriggers.Resume, 4, run.Id, (Guid?)item.MessageId), (row.Trigger, row.BaseStep, row.RunId, row.InboundMessageId));
        Assert.NotEmpty(RunUnitJson.Parse(row.StepsJson));
    }

    [Fact]
    public async Task AnUnconfirmedUnitLeavesNoRow()
    {
        var bed = new Bed(saveFailure: new RequestConcurrencyException("concurrency.conflict", "La conversación cambió."));

        var outcome = await bed.Processor().ProcessAsync(NewItem(), TestContext.Current.CancellationToken);

        Assert.Equal(RunOutcomes.Retry, outcome);
        Assert.Equal(1, bed.Units.Adds);
        Assert.Empty(bed.Units.Saved);
    }

    [Fact]
    public async Task ARecordingFailureDoesNotUndoTheUnit()
    {
        var bed = new Bed(addFailure: new InvalidOperationException(CustomerText));
        var item = NewItem();
        bed.Inbox.Messages[item.MessageId] = new InboundMessage(item.MessageId, CustomerMessageKinds.Text, CustomerText, null, null, DateTimeOffset.UtcNow);

        var outcome = await bed.Processor().ProcessAsync(item, TestContext.Current.CancellationToken);

        var run = Assert.Single(bed.Runs.Added);
        Assert.Equal(RunOutcomes.Started, outcome);
        Assert.Equal([$"complete {item.MessageId} 1 started"], bed.Inbox.Calls);
        Assert.Equal(1, bed.UnitOfWork.Saves);
        Assert.Equal(2, bed.Messenger.Sent.Count);
        Assert.Empty(bed.Units.Saved);
        var warning = Assert.Single(bed.Logs.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Equal(
            $"Run unit of run {run.Id} of tenant {TenantId} was not recorded: InvalidOperationException", warning.Message);
        Assert.Null(warning.Exception);
        Assert.DoesNotContain(bed.Logs.Entries, entry => entry.Message.Contains(CustomerText, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("human")]
    [InlineData("module")]
    [InlineData("sweep")]
    [InlineData("restart")]
    public async Task ClosuresLeaveARowWithoutSteps(string closure)
    {
        var now = DateTimeOffset.UtcNow;
        var item = NewItem();
        var oldVersion = Guid.CreateVersion7();
        var bed = closure switch
        {
            "human" => new Bed(new AutomationConversationState("Open", MemberId, "Paused", ChatbotId, ConnectionId, now, "Laura", "573001234567")),
            "module" => new Bed(ActiveState(), moduleEnabled: false),
            "sweep" => new Bed(ActiveState(now.AddHours(-26))),
            _ => new Bed(ActiveState()),
        };
        var run = closure == "sweep"
            ? LiveRun(RunStatus.Waiting, "q", stepNumber: 2, now.AddHours(-25))
            : LiveRun(RunStatus.Waiting, "q", stepNumber: 2, now.AddMinutes(-1), closure == "restart" ? oldVersion : null);
        bed.Runs.Added.Add(run);
        if (closure == "restart")
        {
            bed.Inbox.Messages[item.MessageId] = new InboundMessage(item.MessageId, CustomerMessageKinds.Text, Chatbot.DefaultRestartKeyword, null, null, now);
        }

        if (closure == "sweep")
        {
            Assert.True(await bed.Processor().AbandonAsync(new AbandonedRun(TenantId, ConversationId, run.Id), now, TestContext.Current.CancellationToken));
        }
        else
        {
            await bed.Processor().ProcessAsync(item, TestContext.Current.CancellationToken);
        }

        var (status, reason, outcome, inbound) = closure switch
        {
            "human" => ("Interrupted", RunCloseReasons.HumanTookOver, RunOutcomes.Interrupted, (Guid?)item.MessageId),
            "module" => ("Interrupted", RunCloseReasons.ModuleDisabled, RunOutcomes.Interrupted, item.MessageId),
            "sweep" => ("Abandoned", RunCloseReasons.Abandoned, RunOutcomes.Ended, null),
            _ => ("Completed", RunCloseReasons.RestartedWithNewVersion, RunOutcomes.Ended, item.MessageId),
        };
        var closed = Assert.Single(bed.Units.Saved, row => row.RunId == run.Id);
        Assert.Equal(
            (TenantId, 2, RunUnitTriggers.Closed, inbound, outcome, "[]", (string?)status, (string?)reason),
            (closed.TenantId, closed.BaseStep, closed.Trigger, closed.InboundMessageId, closed.Outcome, closed.StepsJson, closed.ClosedStatus,
                closed.ClosedReason));

        if (closure == "restart")
        {
            var restarted = Assert.Single(bed.Runs.Added, candidate => candidate.Id != run.Id);
            var start = Assert.Single(bed.Units.Saved, row => row.RunId == restarted.Id);
            Assert.Equal((RunUnitTriggers.Start, 0, (Guid?)item.MessageId), (start.Trigger, start.BaseStep, start.InboundMessageId));
            Assert.Equal(2, bed.Units.Saved.Count);
        }
        else
        {
            Assert.Single(bed.Units.Saved);
        }
    }

    // ---- Dobles ----

    private sealed class Bed(
        AutomationConversationState? state = null, bool moduleEnabled = true, Exception? saveFailure = null, Exception? addFailure = null,
        IHttpBlockExecutor? http = null)
    {
        public MemoryRuns Runs { get; } = new();

        public MemoryRunUnits Units { get; } = new() { AddFailure = addFailure };

        public RecordingInbox Inbox { get; } = new();

        public RecordingMessenger Messenger { get; } = new();

        public CapturedLogs Logs { get; } = new();

        public SavingUnitOfWork UnitOfWork { get; private set; } = null!;

        public RunProcessor Processor()
        {
            UnitOfWork = new SavingUnitOfWork(Units, saveFailure);
            var conversations = new Conversations(state);
            var audit = new NoAudit();
            var hours = new OpenHours();
            var media = new NoMedia();
            var chatbots = new ServingChatbots();
            var clock = new FixedClock();
            var effects = new RunEffects(
                Messenger, conversations, hours, http ?? new OkHttp(), media, audit, new NoDataConsents(), chatbots, NullLogger<RunEffects>.Instance);
            var recorder = new RunUnitWriter(Units, clock, Logs.CreateLogger<RunUnitWriter>());
            return new RunProcessor(
                UnitOfWork, Runs, chatbots, Inbox, conversations, new TenantNames(), new Connections(), hours, media, new ModuleStatus(moduleEnabled),
                audit, effects, clock, NullLogger<RunProcessor>.Instance, recorder);
        }
    }

    /// <summary>Como el contexto de EF: lo agregado sólo queda si la unidad guardó; si la transacción se dispone sin guardar, se va.</summary>
    internal sealed class MemoryRunUnits : IRunUnitRepository
    {
        private readonly List<RunUnitEntry> pending = [];

        public List<RunUnitEntry> Saved { get; } = [];

        public int Adds { get; private set; }

        public Exception? AddFailure { get; init; }

        public void Add(RunUnitEntry entry)
        {
            if (AddFailure is not null)
            {
                throw AddFailure;
            }

            Adds++;
            pending.Add(entry);
        }

        public void Commit()
        {
            Saved.AddRange(pending);
            pending.Clear();
        }

        public void Discard() => pending.Clear();

        public Task<IReadOnlyList<RunUnitEntry>> ListAsync(Guid tenantId, Guid runId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> PurgeBeforeAsync(DateTimeOffset cutoff, int batch, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class SavingUnitOfWork(MemoryRunUnits units, Exception? saveFailure) : IAutomationsUnitOfWork, IAsyncDisposable
    {
        public int Saves { get; private set; }

        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            if (saveFailure is not null)
            {
                return Task.FromException(saveFailure);
            }

            Saves++;
            units.Commit();
            return Task.CompletedTask;
        }

        public Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken cancellationToken) => Task.FromResult<IAsyncDisposable>(this);

        public Task<bool> TryLockConversationAsync(Guid conversationId, CancellationToken cancellationToken) => Task.FromResult(true);

        public ValueTask DisposeAsync()
        {
            units.Discard();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MemoryRuns : IConversationRunRepository
    {
        public List<ConversationRun> Added { get; } = [];

        public Task<ConversationRun?> FindLiveForUpdateAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) =>
            Task.FromResult(Added.FirstOrDefault(run => run.IsLive));

        public Task<ConversationRun?> FindLatestAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) =>
            Task.FromResult(Added.LastOrDefault());

        public Task AddAsync(ConversationRun run, CancellationToken cancellationToken)
        {
            Added.Add(run);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ConversationRun>> ListAsync(Guid tenantId, Guid chatbotId, DateTimeOffset from, DateTimeOffset until, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ConversationRun>> ClaimAbandonedAsync(DateTimeOffset waitingBefore, int limit, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class OkHttp : IHttpBlockExecutor
    {
        public Task<HttpCallResult> ExecuteAsync(HttpCallContext context, Domain.Flow.HttpNode node, RunVariables variables, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpCallResult(true, 200, RunVariables.Empty(), HttpCallOutcomes.Ok, null));
    }

    /// <summary>Publica siempre <see cref="VersionId"/>: una corrida de otra versión ve una más nueva al reiniciar.</summary>
    private sealed class ServingChatbots : IChatbotRepository
    {
        public Task<ServingChatbot?> FindServingByConnectionAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
            Task.FromResult<ServingChatbot?>(new ServingChatbot(
                ChatbotId, "Servicio", ChatbotStatus.Active, ChatbotTrigger.NewConversation, [], Chatbot.DefaultRestartKeyword, 3, VersionId));

        public Task<string?> GetVersionFlowAsync(Guid tenantId, Guid versionId, CancellationToken cancellationToken) => Task.FromResult<string?>(Flow);

        public Task<EngineSettings?> FindEngineSettingsAsync(Guid tenantId, Guid chatbotId, CancellationToken cancellationToken) =>
            Task.FromResult<EngineSettings?>(new EngineSettings(Chatbot.DefaultRestartKeyword, 3));

        public Task<ChatbotVersionLabel?> FindVersionLabelAsync(Guid tenantId, Guid versionId, CancellationToken cancellationToken) =>
            Task.FromResult<ChatbotVersionLabel?>(null);

        public Task<Chatbot?> FindAsync(Guid tenantId, Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Chatbot?> FindPublishedByConnectionAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<ChatbotSummaryRow>> ListSummariesAsync(Guid tenantId, string? search, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<ChatbotServingConnection>> ListServingConnectionsAsync(Guid tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task AddAsync(Chatbot chatbot, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void Remove(Chatbot chatbot) => throw new NotSupportedException();

        public Task<bool> NameExistsAsync(Guid tenantId, string name, Guid? exceptId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingInbox : IAutomationInboundQueue
    {
        public List<string> Calls { get; } = [];

        public Dictionary<Guid, InboundMessage> Messages { get; } = [];

        public Task<IReadOnlyList<InboundQueueItem>> ClaimAsync(DateTimeOffset now, int batch, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> CompleteAsync(Guid tenantId, Guid messageId, int attempts, string outcome, CancellationToken cancellationToken)
        {
            Calls.Add($"complete {messageId} {attempts} {outcome}");
            return Task.FromResult(true);
        }

        public Task<bool> RetryLaterAsync(Guid tenantId, Guid messageId, int attempts, TimeSpan delay, CancellationToken cancellationToken)
        {
            Calls.Add($"retry {messageId} {attempts} {delay}");
            return Task.FromResult(true);
        }

        public Task<InboundMessage?> ReadAsync(Guid tenantId, Guid messageId, CancellationToken cancellationToken) =>
            Task.FromResult<InboundMessage?>(Messages.TryGetValue(messageId, out var message)
                ? message
                : new InboundMessage(messageId, CustomerMessageKinds.Text, "Hola", null, null, DateTimeOffset.UtcNow));

        public Task<int> PurgeProcessedBeforeAsync(DateTimeOffset cutoff, int batch, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Conversations(AutomationConversationState? state) : IAutomationConversations
    {
        public Task<AutomationConversationState?> GetAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) =>
            Task.FromResult<AutomationConversationState?>(state ?? new AutomationConversationState(
                "Open", null, "None", null, ConnectionId, DateTimeOffset.UtcNow, "Laura", "573001234567"));

        public Task StartAsync(Guid tenantId, Guid conversationId, Guid chatbotId, string? firstStepTitle, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetStepAsync(Guid tenantId, Guid conversationId, string? stepTitle, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetChatbotAsync(Guid tenantId, Guid conversationId, Guid chatbotId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task HandoffAsync(Guid tenantId, Guid conversationId, HandoffCause cause, Guid? assignToMemberId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task ResolveAsync(Guid tenantId, Guid conversationId, Guid? reasonId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task EndAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingMessenger : IAutomationMessenger
    {
        public List<Guid> Sent { get; } = [];

        public Task<BotSendOutcome> SendTextAsync(Guid tenantId, Guid conversationId, Guid chatbotId, Guid clientId, string text, CancellationToken cancellationToken)
        {
            Sent.Add(clientId);
            return Task.FromResult<BotSendOutcome>(new BotSendOutcome.Sent(Guid.CreateVersion7()));
        }

        public Task<BotSendOutcome> SendInteractiveAsync(
            Guid tenantId, Guid conversationId, Guid chatbotId, Guid clientId, InteractiveMessage message, CancellationToken cancellationToken)
        {
            Sent.Add(clientId);
            return Task.FromResult<BotSendOutcome>(new BotSendOutcome.Sent(Guid.CreateVersion7()));
        }
    }

    private sealed class OpenHours : IAutomationBusinessHours
    {
        public Task<IReadOnlyDictionary<Guid, string?>> ScheduleNamesAsync(Guid tenantId, IReadOnlyCollection<Guid> connectionIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> IsOpenAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class TenantNames : IAutomationTenantNames
    {
        public Task<string?> FindAsync(Guid tenantId, CancellationToken cancellationToken) => Task.FromResult<string?>("Clasic");
    }

    private sealed class Connections : IAutomationConnections
    {
        public Task<IReadOnlyList<AutomationConnectionRef>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AutomationConnectionRef>>([new AutomationConnectionRef(ConnectionId, "Servicio", "WhatsApp", "+57", "Active")]);
    }

    private sealed class NoMedia : IAutomationMedia
    {
        public Task<StoredAttachment?> TryGetStoredAsync(Guid tenantId, Guid messageId, CancellationToken cancellationToken) => Task.FromResult<StoredAttachment?>(null);

        public Task<Uri> CreateDownloadUrlAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<(Stream Content, long Length)?> OpenReadAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class ModuleStatus(bool enabled) : IAutomationsModuleStatus
    {
        public Task<bool> IsEnabledAsync(Guid tenantId, CancellationToken cancellationToken) => Task.FromResult(enabled);
    }

    private sealed class NoAudit : IAutomationsAuditRecorder
    {
        public void Record(Guid tenantId, Guid actorId, bool bySystem, string action, string resourceType, Guid resourceId, DateTimeOffset at)
        {
        }
    }

    private sealed class NoDataConsents : IAutomationDataConsents
    {
        public Task<DataConsentCheck> CheckAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> RecordAsync(DataConsentRecord record, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = DateTimeOffset.UtcNow;
    }

    /// <summary>Cada entrada con su nivel, el mensaje formateado y la excepción que se pasó (si alguna).</summary>
    internal sealed class CapturedLogs
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public ILogger<T> CreateLogger<T>() => new Logger<T>(this);

        private sealed class Logger<T>(CapturedLogs sink) : ILogger<T>
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                sink.Entries.Add((logLevel, formatter(state, exception), exception));
        }
    }
}
