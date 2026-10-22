using BuildingBlocks.Application;
using BuildingBlocks.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Modules.Automations.Application;
using Modules.Automations.Application.Engine;
using Modules.Automations.Application.Http;
using Modules.Automations.Domain;
using Modules.Automations.Domain.Engine;

namespace Modules.Automations.UnitTests;

/// <summary>
/// Spec 2026-10-21 §5.2 y D-M4: los caminos del arranque que de punta a punta no se pueden provocar a voluntad (el cambio de la
/// conversación tiene que caer entre la lectura del procesador y el guardado de Mensajería). Con dobles en memoria de todos los puertos.
/// </summary>
public sealed class RunProcessorTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ConnectionId = Guid.CreateVersion7();
    private static readonly Guid ConversationId = Guid.CreateVersion7();
    private static readonly Guid ChatbotId = Guid.CreateVersion7();
    private static readonly Guid VersionId = Guid.CreateVersion7();

    private const string Flow = """
        { "schema": 1, "start": { "firstNodeId": "n1" }, "variables": [],
          "nodes": [ { "id": "n1", "kind": "message", "title": "Hola", "text": "Hola", "outputs": { "next": null } } ] }
        """;

    private static readonly InboundQueueItem Item = new(Guid.CreateVersion7(), TenantId, ConnectionId, ConversationId, Opened: true, DateTimeOffset.UtcNow, Attempts: 1);

    /// <summary>D-M4: la ingesta subió la versión entre la lectura y el guardado de Mensajería; la fila se reprograma a 2 s con su testigo y
    /// no se cierra.</summary>
    [Fact]
    public async Task AConcurrencyConflictOnStartRetriesTheRowInTwoSeconds()
    {
        var bed = new Bed(new RequestConcurrencyException("concurrency.conflict", "La conversación cambió."));

        var outcome = await bed.Processor().ProcessAsync(Item, TestContext.Current.CancellationToken);

        Assert.Equal(RunOutcomes.Retry, outcome);
        Assert.Equal([$"retry {Item.MessageId} 1 00:00:02"], bed.Inbox.Calls);
        Assert.Empty(bed.Runs.Added);
        Assert.Empty(bed.Messenger.Sent);
    }

    /// <summary>
    /// D-M2 y P2-R5: el bloque HTTP vuelve a entrar al motor con su resultado dentro de la misma unidad, y los envíos que siguen continúan la
    /// cuenta (un reintento de la fila repite los mismos clientIds en vez de reusar el 0 con otro mensaje). El ejecutor recibe la corrida y
    /// las variables guardadas hasta ahí; lo mapeado sólo entra si salió bien.
    /// </summary>
    [Fact]
    public async Task AnHttpBlockReentersTheEngineAndTheSendsAfterItKeepCounting()
    {
        const string flow = """
            { "schema": 1, "start": { "firstNodeId": "n1" }, "variables": [],
              "nodes": [
                { "id": "n1", "kind": "message", "title": "Hola", "text": "Hola", "outputs": { "next": "n2" } },
                { "id": "n2", "kind": "http", "title": "Buscar", "connectionId": "11111111-1111-1111-1111-111111111111", "method": "GET", "path": "/customers", "mapping": [ { "from": "data", "to": "cliente" } ], "outputs": { "ok": "n3", "error": null } },
                { "id": "n3", "kind": "message", "title": "Encontrado", "text": "Te encontramos, {cliente.firstName}.", "outputs": { "next": null } }
              ] }
            """;
        var mapped = RunVariables.Empty();
        mapped.Set("cliente", System.Text.Json.Nodes.JsonNode.Parse("""{"firstName":"Laura"}"""));
        var http = new RecordingHttpBlocks(new HttpCallResult(true, 200, mapped, HttpCallOutcomes.Ok, null));
        var bed = new Bed(null, flow: flow, http: http);

        var outcome = await bed.Processor().ProcessAsync(Item, TestContext.Current.CancellationToken);

        var run = Assert.Single(bed.Runs.Added);
        Assert.Equal(RunOutcomes.Ended, outcome);
        Assert.Equal([DeterministicClientId.For(run.Id, 0, 0), DeterministicClientId.For(run.Id, 0, 1)], bed.Messenger.Sent);
        var (context, variables) = Assert.Single(http.Calls);
        Assert.Equal(new HttpCallContext(TenantId, run.Id, null, "n2"), context);
        Assert.Equal("Laura", variables.TextOf("nombre"));
        Assert.Equal("Laura", run.ToState().Variables.TextOf("cliente.firstName"));
    }

    /// <summary>
    /// Ruling P2-R40: el motor corta un ciclo dentro de un recorrido (P2-R13), pero cada resultado de un HTTP empieza un recorrido nuevo, así
    /// que un bloque cuya salida vuelve a él daría vueltas dentro de la unidad para siempre (pasado el lease de 30 s). A las 25 reentradas la
    /// corrida termina Failed <c>loop_detected</c> como en P2-R13: traspaso a un asesor y auditoría, sin más envíos.
    /// </summary>
    [Fact]
    public async Task AnHttpBlockThatLoopsToItselfEndsFailedAfterTheReentryCap()
    {
        const string flow = """
            { "schema": 1, "start": { "firstNodeId": "n1" }, "variables": [],
              "nodes": [
                { "id": "n1", "kind": "message", "title": "Hola", "text": "Hola", "outputs": { "next": "n2" } },
                { "id": "n2", "kind": "http", "title": "Consultar", "connectionId": "11111111-1111-1111-1111-111111111111", "method": "GET", "path": "/status", "mapping": [], "outputs": { "ok": "n2", "error": null } }
              ] }
            """;
        var http = new RecordingHttpBlocks(new HttpCallResult(true, 200, RunVariables.Empty(), HttpCallOutcomes.Ok, null));
        var bed = new Bed(null, flow: flow, http: http);

        var outcome = await bed.Processor().ProcessAsync(Item, TestContext.Current.CancellationToken);

        var run = Assert.Single(bed.Runs.Added);
        Assert.Equal(RunOutcomes.Failed, outcome);
        Assert.Equal((RunStatus.Failed, "automations.run.loop_detected"), (run.Status, run.FailureCode));
        Assert.Equal(EngineReentries.Max, http.Calls.Count);
        Assert.Equal(25, EngineReentries.Max);
        Assert.Equal(1, bed.Conversations.HandedOff);
        Assert.Equal([DeterministicClientId.For(run.Id, 0, 0)], bed.Messenger.Sent);
        Assert.Equal([AutomationsAuditActions.RunStarted, AutomationsAuditActions.RunFailed], bed.Audit.Actions);
    }

    /// <summary>D-H4: un fallo de la API sale por <c>error</c> sin mezclar nada de lo que el ejecutor haya devuelto.</summary>
    [Fact]
    public async Task AFailedHttpBlockTakesTheErrorOutputWithoutMerging()
    {
        const string flow = """
            { "schema": 1, "start": { "firstNodeId": "n1" }, "variables": [],
              "nodes": [
                { "id": "n1", "kind": "http", "title": "Buscar", "connectionId": "11111111-1111-1111-1111-111111111111", "method": "GET", "path": "/customers", "mapping": [ { "from": "data", "to": "cliente" } ], "outputs": { "ok": null, "error": "n2" } },
                { "id": "n2", "kind": "message", "title": "Error", "text": "No pudimos buscarte.", "outputs": { "next": null } }
              ] }
            """;
        var leaked = RunVariables.Empty();
        leaked.Set("cliente", System.Text.Json.Nodes.JsonNode.Parse("""{"firstName":"Laura"}"""));
        var bed = new Bed(null, flow: flow, http: new RecordingHttpBlocks(new HttpCallResult(false, 409, leaked, HttpCallOutcomes.ErrorStatus, "http_409")));

        await bed.Processor().ProcessAsync(Item, TestContext.Current.CancellationToken);

        var run = Assert.Single(bed.Runs.Added);
        Assert.Single(bed.Messenger.Sent);
        Assert.Null(run.ToState().Variables.Get("cliente"));
    }

    /// <summary>Lista vinculante de la tarea 5: una persona tomó o resolvió la conversación entre la lectura y el arranque.</summary>
    [Fact]
    public async Task ADomainRuleOnStartSkipsTheRow()
    {
        var bed = new Bed(new TestDomainException());

        var outcome = await bed.Processor().ProcessAsync(Item, TestContext.Current.CancellationToken);

        Assert.Equal(RunOutcomes.Skipped, outcome);
        Assert.Equal([$"complete {Item.MessageId} 1 skipped"], bed.Inbox.Calls);
        Assert.Empty(bed.Runs.Added);
        Assert.Empty(bed.Messenger.Sent);
    }

    /// <summary>
    /// D-M15 y P2-R25 (minor de la tarea 6): una persona devuelve la conversación al asistente mientras la unidad espera el lock. La hora
    /// de la corrida se lee después del lock y de la conversación; si se leyera antes, la corrida nueva arrancaría antes que el regreso y el
    /// siguiente entrante la interrumpiría como si una persona la hubiera devuelto después.
    /// </summary>
    [Fact]
    public async Task TheRunStartsAfterAResumeStampedWhileTheUnitWaitedForTheLock()
    {
        var clock = new SteppingClock(DateTimeOffset.UtcNow);
        var resumedAt = clock.UtcNow.AddSeconds(30);
        var bed = new Bed(
            startFailure: null,
            new AutomationConversationState("Open", null, "Active", ChatbotId, ConnectionId, DateTimeOffset.UtcNow, "Laura", "573001234567", resumedAt),
            clock,
            onLock: () => clock.UtcNow = clock.UtcNow.AddMinutes(1));

        var outcome = await bed.Processor().ProcessAsync(Item, TestContext.Current.CancellationToken);

        Assert.Equal(RunOutcomes.Ended, outcome);
        var run = Assert.Single(bed.Runs.Added);
        Assert.True(run.StartedAt >= resumedAt, $"La corrida arrancó a las {run.StartedAt:O}, antes del regreso de las {resumedAt:O}.");
    }

    /// <summary>D-M13 y D-M14: la candidata sigue esperando desde hace más de 24 h bajo el lock: termina <c>Abandoned</c>, la conversación
    /// vuelve a None y queda la auditoría de sistema.</summary>
    [Fact]
    public async Task AStaleWaitingRunIsAbandoned()
    {
        var now = DateTimeOffset.UtcNow;
        // Con el bot atendiendo (Active) y sin devolución humana posterior al arranque de la corrida.
        var bed = new Bed(
            startFailure: null,
            new AutomationConversationState("Open", null, "Active", ChatbotId, ConnectionId, now.AddHours(-26), "Laura", "573001234567"));
        var run = WaitingRun(now.AddHours(-25));
        bed.Runs.Added.Add(run);

        Assert.True(await bed.Processor().AbandonAsync(new AbandonedRun(TenantId, ConversationId, run.Id), now, TestContext.Current.CancellationToken));

        Assert.Equal(RunStatus.Abandoned, run.Status);
        Assert.Equal(now, run.EndedAt);
        Assert.Equal(1, bed.Conversations.Ended);
        Assert.Equal([AutomationsAuditActions.RunAbandoned], bed.Audit.Actions);
    }

    /// <summary>D-M15 y P2-R25: una persona la tomó, la atendió más de 24 h y la devolvió al asistente; la corrida vieja sigue en espera. El
    /// barrido la termina <c>Interrupted</c> sin llamar a <c>EndAsync</c>: la conversación se queda <c>Active</c> para que el siguiente
    /// entrante arranque desde el primer nodo, y no queda auditoría de abandono.</summary>
    [Fact]
    public async Task ARunResumedByAHumanIsInterruptedNotAbandoned()
    {
        var now = DateTimeOffset.UtcNow;
        var bed = new Bed(
            startFailure: null,
            new AutomationConversationState("Open", null, "Active", ChatbotId, ConnectionId, now.AddHours(-30), "Laura", "573001234567", now.AddMinutes(-5)));
        var run = WaitingRun(now.AddHours(-25));
        bed.Runs.Added.Add(run);

        Assert.True(await bed.Processor().AbandonAsync(new AbandonedRun(TenantId, ConversationId, run.Id), now, TestContext.Current.CancellationToken));

        Assert.Equal(RunStatus.Interrupted, run.Status);
        Assert.Equal(0, bed.Conversations.Ended);
        Assert.Empty(bed.Audit.Actions);
    }

    /// <summary>D-M15: si una persona la tiene (pausada, con asesor o resuelta), la corrida termina <c>Interrupted</c> y no <c>Abandoned</c>.</summary>
    [Theory]
    [InlineData("Open", "Paused")]
    [InlineData("Resolved", "Active")]
    public async Task ARunOfAConversationAHumanHoldsIsInterruptedNotAbandoned(string status, string automationStatus)
    {
        var now = DateTimeOffset.UtcNow;
        var bed = new Bed(
            startFailure: null,
            new AutomationConversationState(status, null, automationStatus, ChatbotId, ConnectionId, now.AddHours(-30), "Laura", "573001234567"));
        var run = WaitingRun(now.AddHours(-25));
        bed.Runs.Added.Add(run);

        Assert.True(await bed.Processor().AbandonAsync(new AbandonedRun(TenantId, ConversationId, run.Id), now, TestContext.Current.CancellationToken));

        Assert.Equal(RunStatus.Interrupted, run.Status);
        Assert.Equal(0, bed.Conversations.Ended);
        Assert.Empty(bed.Audit.Actions);
    }

    /// <summary>P2-R20: entre la elección y el lock un entrante movió la corrida (o es otra la viva); ya no está abandonada y no se toca.</summary>
    [Fact]
    public async Task ARunThatMovedAfterItWasPickedIsNotAbandoned()
    {
        var now = DateTimeOffset.UtcNow;
        var bed = new Bed(startFailure: null);
        var run = WaitingRun(now.AddMinutes(-1));
        bed.Runs.Added.Add(run);

        Assert.False(await bed.Processor().AbandonAsync(new AbandonedRun(TenantId, ConversationId, run.Id), now, TestContext.Current.CancellationToken));
        Assert.False(await bed.Processor().AbandonAsync(new AbandonedRun(TenantId, ConversationId, Guid.CreateVersion7()), now.AddDays(2), TestContext.Current.CancellationToken));

        Assert.Equal(RunStatus.Waiting, run.Status);
        Assert.Equal(0, bed.Conversations.Ended);
        Assert.Empty(bed.Audit.Actions);
    }

    private static ConversationRun WaitingRun(DateTimeOffset updatedAt) =>
        ConversationRun.Start(
            Guid.CreateVersion7(), TenantId, ConversationId, ConnectionId, ChatbotId, VersionId,
            RunState.Initial(RunVariables.Empty()) with { Status = RunStatus.Waiting, CurrentNodeId = "n1" }, updatedAt);

    private sealed class TestDomainException() : DomainException("messaging.automation.not_available", "Ya no es del bot.");

    private sealed class Bed(
        Exception? startFailure, AutomationConversationState? state = null, IClock? clock = null, Action? onLock = null, string flow = Flow,
        IHttpBlockExecutor? http = null)
    {
        public RecordingInbox Inbox { get; } = new();

        public MemoryRuns Runs { get; } = new();

        public RecordingMessenger Messenger { get; } = new();

        public Conversations Conversations { get; } = new(startFailure, state);

        public RecordingAudit Audit { get; } = new();

        public RunProcessor Processor()
        {
            var conversations = Conversations;
            var audit = Audit;
            var hours = new OpenHours();
            var effects = new RunEffects(Messenger, conversations, hours, http ?? new RecordingHttpBlocks(null), audit, NullLogger<RunEffects>.Instance);
            return new RunProcessor(
                new NoUnitOfWork(onLock), Runs, new ServingChatbots(flow), Inbox, conversations, new TenantNames(), new Connections(), hours,
                new NoMedia(), audit, effects, clock ?? new FixedClock(), NullLogger<RunProcessor>.Instance);
        }
    }

    private sealed class NoUnitOfWork(Action? onLock) : IAutomationsUnitOfWork, IAsyncDisposable
    {
        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken cancellationToken) => Task.FromResult<IAsyncDisposable>(this);

        public Task LockConversationAsync(Guid conversationId, CancellationToken cancellationToken)
        {
            onLock?.Invoke();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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

    /// <summary>El ejecutor del bloque HTTP: anota cada llamada y responde <paramref name="result"/> (sin resultado, ningún flujo de la prueba
    /// tiene un bloque HTTP).</summary>
    private sealed class RecordingHttpBlocks(HttpCallResult? result) : IHttpBlockExecutor
    {
        public List<(HttpCallContext Context, RunVariables Variables)> Calls { get; } = [];

        public Task<HttpCallResult> ExecuteAsync(HttpCallContext context, Domain.Flow.HttpNode node, RunVariables variables, CancellationToken cancellationToken)
        {
            Calls.Add((context, variables));
            return Task.FromResult(result ?? throw new NotSupportedException("La prueba no esperaba un bloque HTTP."));
        }
    }

    private sealed class ServingChatbots(string flow) : IChatbotRepository
    {
        public Task<ServingChatbot?> FindServingByConnectionAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
            Task.FromResult<ServingChatbot?>(new ServingChatbot(
                ChatbotId, "Servicio", ChatbotStatus.Active, ChatbotTrigger.NewConversation, [], Chatbot.DefaultRestartKeyword, 3, VersionId));

        public Task<string?> GetVersionFlowAsync(Guid tenantId, Guid versionId, CancellationToken cancellationToken) => Task.FromResult<string?>(flow);

        public Task<EngineSettings?> FindEngineSettingsAsync(Guid tenantId, Guid chatbotId, CancellationToken cancellationToken) =>
            Task.FromResult<EngineSettings?>(new EngineSettings(Chatbot.DefaultRestartKeyword, 3));

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
            Task.FromResult<InboundMessage?>(new InboundMessage(messageId, CustomerMessageKinds.Text, "Hola", null, null, DateTimeOffset.UtcNow));

        public Task<int> PurgeProcessedBeforeAsync(DateTimeOffset cutoff, int batch, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Conversations(Exception? startFailure, AutomationConversationState? state) : IAutomationConversations
    {
        public int Ended { get; private set; }

        public int HandedOff { get; private set; }

        public Task<AutomationConversationState?> GetAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) =>
            Task.FromResult<AutomationConversationState?>(state ?? new AutomationConversationState(
                "Open", null, "None", null, ConnectionId, DateTimeOffset.UtcNow, "Laura", "573001234567"));

        public Task StartAsync(Guid tenantId, Guid conversationId, Guid chatbotId, string? firstStepTitle, CancellationToken cancellationToken) =>
            startFailure is null ? Task.CompletedTask : Task.FromException(startFailure);

        public Task SetStepAsync(Guid tenantId, Guid conversationId, string? stepTitle, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetChatbotAsync(Guid tenantId, Guid conversationId, Guid chatbotId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task HandoffAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken)
        {
            HandedOff++;
            return Task.CompletedTask;
        }

        public Task ResolveAsync(Guid tenantId, Guid conversationId, Guid? reasonId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task EndAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken)
        {
            Ended++;
            return Task.CompletedTask;
        }
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

    private sealed class RecordingAudit : IAutomationsAuditRecorder
    {
        public List<string> Actions { get; } = [];

        public void Record(Guid tenantId, Guid actorId, bool bySystem, string action, string resourceType, Guid resourceId, DateTimeOffset at) =>
            Actions.Add(action);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = DateTimeOffset.UtcNow;
    }

    /// <summary>Un reloj que la prueba adelanta (desde el lock, en la prueba del regreso al asistente).</summary>
    private sealed class SteppingClock(DateTimeOffset start) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = start;
    }
}
