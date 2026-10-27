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
        Assert.Equal(new HttpCallContext(TenantId, run.Id, null, "n2", StepNumber: 2), context);
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
        // P2-R46: «run_failed», distinto del «failed» con que Mensajería cierra una fila que agotó sus intentos.
        Assert.Equal("run_failed", outcome);
        Assert.Equal([$"complete {Item.MessageId} 1 run_failed"], bed.Inbox.Calls);
        Assert.Equal((RunStatus.Failed, "automations.run.loop_detected"), (run.Status, run.FailureCode));
        Assert.Equal(EngineReentries.Max, http.Calls.Count);
        Assert.Equal(25, EngineReentries.Max);
        Assert.Equal(1, bed.Conversations.HandedOff);
        Assert.Equal([DeterministicClientId.For(run.Id, 0, 0)], bed.Messenger.Sent);
        Assert.Equal([AutomationsAuditActions.RunStarted, AutomationsAuditActions.RunFailed], bed.Audit.Actions);
    }

    // ---- Spec 2026-10-24 §4.1: una causa por cada camino a HandoffAsync. ----

    /// <summary>Fila 5 (P2-R40): el bloque que pidió la reentrada 26; el paso empezó con un HTTP que salió bien, así que sin <c>Http</c>.</summary>
    [Fact]
    public async Task TheReentryCapSaysFlowFailedLoopDetected()
    {
        const string flow = """
            { "schema": 1, "start": { "firstNodeId": "n2" }, "variables": [],
              "nodes": [
                { "id": "n2", "kind": "http", "title": "Consultar", "connectionId": "11111111-1111-1111-1111-111111111111", "method": "GET", "path": "/status", "mapping": [], "outputs": { "ok": "n2", "error": null } }
              ] }
            """;
        var bed = new Bed(null, flow: flow, http: new RecordingHttpBlocks(new HttpCallResult(true, 200, RunVariables.Empty(), HttpCallOutcomes.Ok, null)));

        await bed.Processor().ProcessAsync(Item, TestContext.Current.CancellationToken);

        Assert.Equal(
            new HandoffCause(HandoffCauseKind.FlowFailed, "automations.run.loop_detected", "n2", "Consultar", null, null, null),
            Assert.Single(bed.Conversations.Causes));
        Assert.Equal("automations.run.loop_detected", Assert.Single(bed.Runs.Added).FailureCode);
    }

    private const string HandoffAfterHttpFlow = """
        { "schema": 1, "start": { "firstNodeId": "policy-lookup" }, "variables": [],
          "nodes": [
            { "id": "policy-lookup", "kind": "http", "title": "Buscar cliente por teléfono", "connectionId": "11111111-1111-1111-1111-111111111111", "method": "GET", "path": "/customers", "mapping": [], "outputs": { "ok": null, "error": "handoff" } },
            { "id": "handoff", "kind": "handoff", "title": "Pasar a un asesor", "text": "Te paso con un asesor.", "outputs": {} }
          ] }
        """;

    /// <summary>Fila 1: el flujo llegó al bloque de traspaso a propósito; sin HTTP ni código.</summary>
    [Fact]
    public async Task AHandoffBlockReachedOnPurposeSaysBlock()
    {
        const string flow = """
            { "schema": 1, "start": { "firstNodeId": "n1" }, "variables": [],
              "nodes": [
                { "id": "n1", "kind": "message", "title": "Hola", "text": "Hola", "outputs": { "next": "n2" } },
                { "id": "n2", "kind": "handoff", "title": "Pasar a un asesor", "text": null, "outputs": {} }
              ] }
            """;
        var bed = new Bed(null, flow: flow);

        await bed.Processor().ProcessAsync(Item, TestContext.Current.CancellationToken);

        Assert.Equal(new HandoffCause(HandoffCauseKind.Block, null, "n2", "Pasar a un asesor", null, null, null), Assert.Single(bed.Conversations.Causes));
        var run = Assert.Single(bed.Runs.Added);
        Assert.Equal((RunStatus.HandedOff, (string?)null), (run.Status, run.FailureCode));
    }

    /// <summary>Spec 2026-10-25 D-1: el bloque de traspaso trae el miembro elegido y el procesador se lo pasa al adaptador.</summary>
    [Fact]
    public async Task AHandoffBlockPassesItsMember()
    {
        var member = Guid.CreateVersion7();
        var flow = $$"""
            { "schema": 1, "start": { "firstNodeId": "n2" }, "variables": [],
              "nodes": [
                { "id": "n2", "kind": "handoff", "title": "Pasar a un asesor", "text": null, "memberId": "{{member}}", "outputs": {} }
              ] }
            """;
        var bed = new Bed(null, flow: flow);

        await bed.Processor().ProcessAsync(Item, TestContext.Current.CancellationToken);

        Assert.Equal(
            (new HandoffCause(HandoffCauseKind.Block, null, "n2", "Pasar a un asesor", null, null, null), (Guid?)member),
            Assert.Single(bed.Conversations.Handoffs));
    }

    /// <summary>Spec 2026-10-25 D-4: la pregunta sin salida <c>invalid</c> que agota sus intentos traspasa con la causa
    /// <c>AttemptsExhausted</c> y el miembro que dice la regla del chatbot.</summary>
    [Fact]
    public async Task AnExhaustedQuestionHandsOffWithItsCauseAndMember()
    {
        var member = Guid.CreateVersion7();
        const string flow = """
            { "schema": 1, "start": { "firstNodeId": "q" }, "variables": [],
              "nodes": [ { "id": "q", "kind": "openQuestion", "title": "Documento", "text": "¿Cuál es tu documento?", "saveTo": "doc",
                "validation": { "kind": "regex", "pattern": "^[0-9]{6,10}$", "keywords": null }, "outputs": { "answered": null, "invalid": null } } ] }
            """;
        var bed = new Bed(
            null, ActiveState(), flow: flow, engineSettings: new EngineSettings(Chatbot.DefaultRestartKeyword, 3, AttemptsExhaustedAction.Handoff, member));
        bed.Runs.Added.Add(ConversationRun.Start(
            Guid.CreateVersion7(), TenantId, ConversationId, ConnectionId, ChatbotId, VersionId,
            RunState.Initial(RunVariables.Empty()) with { Status = RunStatus.Waiting, CurrentNodeId = "q", Attempts = 2 }, DateTimeOffset.UtcNow.AddMinutes(-1)));

        await bed.Processor().ProcessAsync(Item, TestContext.Current.CancellationToken);

        Assert.Equal(
            (new HandoffCause(HandoffCauseKind.AttemptsExhausted, null, "q", "Documento", null, null, null), (Guid?)member),
            Assert.Single(bed.Conversations.Handoffs));
    }

    /// <summary>Spec 2026-10-25 D-1: un fallo nunca asigna, aunque el chatbot tenga un miembro en su regla de agotamiento.</summary>
    [Fact]
    public async Task AFailureNeverAssigns()
    {
        const string flow = """
            { "schema": 1, "start": { "firstNodeId": "n2" }, "variables": [],
              "nodes": [
                { "id": "n2", "kind": "http", "title": "Consultar", "connectionId": "11111111-1111-1111-1111-111111111111", "method": "GET", "path": "/status", "mapping": [], "outputs": { "ok": "n2", "error": null } }
              ] }
            """;
        var bed = new Bed(
            null, flow: flow, http: new RecordingHttpBlocks(new HttpCallResult(true, 200, RunVariables.Empty(), HttpCallOutcomes.Ok, null)),
            engineSettings: new EngineSettings(Chatbot.DefaultRestartKeyword, 3, AttemptsExhaustedAction.Handoff, Guid.CreateVersion7()));

        await bed.Processor().ProcessAsync(Item, TestContext.Current.CancellationToken);

        Assert.Equal(HandoffCauseKind.FlowFailed, Assert.Single(bed.Conversations.Handoffs).Cause.Kind);
        Assert.Null(bed.Conversations.Handoffs[0].Member);
    }

    /// <summary>Fila 1 con HTTP (caso 2 del owner): el paso empezó con el 404 del bloque HTTP.</summary>
    [Fact]
    public async Task AHandoffBlockAfterAFailedHttpNamesTheHttpBlock()
    {
        var bed = new Bed(null, flow: HandoffAfterHttpFlow, http: new RecordingHttpBlocks(
            new HttpCallResult(false, 404, RunVariables.Empty(), HttpCallOutcomes.ErrorStatus, "http_404", "API de Clasic")));

        await bed.Processor().ProcessAsync(Item, TestContext.Current.CancellationToken);

        Assert.Equal(
            new HandoffCause(
                HandoffCauseKind.Block, null, "handoff", "Pasar a un asesor", null, null,
                new HandoffHttpFailure("policy-lookup", "Buscar cliente por teléfono", "error_status", "http_404", 404, "API de Clasic")),
            Assert.Single(bed.Conversations.Causes));
        Assert.Equal(["Te paso con un asesor."], bed.Messenger.Texts);
    }

    /// <summary>Fila 2: Botones con <c>fallback = handoff</c> y una respuesta que no es ninguna opción.</summary>
    [Fact]
    public async Task AnInvalidReplyWithHandoffFallbackSaysInvalidReply()
    {
        const string flow = """
            { "schema": 1, "start": { "firstNodeId": "b" }, "variables": [],
              "nodes": [
                { "id": "b", "kind": "buttons", "title": "¿Cómo seguimos?", "text": "¿Qué prefieres?", "buttons": [ { "id": "si", "label": "Sí" } ],
                  "saveTo": null, "fallback": "handoff", "outputs": { "si": null, "other": null } }
              ] }
            """;
        var bed = new Bed(null, ActiveState(), flow: flow);
        bed.Runs.Added.Add(WaitingRun(DateTimeOffset.UtcNow.AddMinutes(-1), nodeId: "b"));

        await bed.Processor().ProcessAsync(Item, TestContext.Current.CancellationToken);

        Assert.Equal(
            new HandoffCause(HandoffCauseKind.InvalidReply, null, "b", "¿Cómo seguimos?", null, null, null), Assert.Single(bed.Conversations.Causes));
    }

    /// <summary>Fila 3 (caso 1 del owner): la clave de la API y el token de WhatsApp ilegibles. El texto del traspaso no sale y la causa
    /// lleva las dos conexiones.</summary>
    [Fact]
    public async Task ARejectedSendSaysSendFailedWithTheConnectionReason()
    {
        var bed = new Bed(null, flow: HandoffAfterHttpFlow, http: new RecordingHttpBlocks(
            new HttpCallResult(false, null, RunVariables.Empty(), HttpCallOutcomes.ConnectionUnavailable, "secret_unreadable", "API de Clasic")));
        bed.Messenger.Reply = _ => new BotSendOutcome.Rejected("messaging.connection_unavailable", "secret_unreadable", "Servicio Clasic");

        await bed.Processor().ProcessAsync(Item, TestContext.Current.CancellationToken);

        Assert.Equal(
            new HandoffCause(
                HandoffCauseKind.SendFailed, "messaging.connection_unavailable", "handoff", "Pasar a un asesor", "Servicio Clasic", "secret_unreadable",
                new HandoffHttpFailure("policy-lookup", "Buscar cliente por teléfono", "connection_unavailable", "secret_unreadable", null, "API de Clasic")),
            Assert.Single(bed.Conversations.Causes));
        var run = Assert.Single(bed.Runs.Added);
        Assert.Equal((RunStatus.Failed, "messaging.connection_unavailable"), (run.Status, run.FailureCode));
    }

    /// <summary>Spec 2026-10-26 §3.4: el contacto quedó bloqueado entre el reclamo y el envío. No es un fallo ni un traspaso (nadie tiene que
    /// atenderlo): la corrida termina <c>Interrupted</c> en silencio y la conversación no cambia de estado.</summary>
    [Fact]
    public async Task ABlockedSendInterruptsTheRunWithoutHandingOff()
    {
        var bed = new Bed(null);
        bed.Messenger.Reply = _ => new BotSendOutcome.Blocked();

        await bed.Processor().ProcessAsync(Item, TestContext.Current.CancellationToken);

        var run = Assert.Single(bed.Runs.Added);
        Assert.Equal((RunStatus.Interrupted, (string?)null), (run.Status, run.FailureCode));
        Assert.Equal((0, 0), (bed.Conversations.HandedOff, bed.Conversations.Ended));
        Assert.DoesNotContain(AutomationsAuditActions.RunFailed, bed.Audit.Actions);
    }

    /// <summary>D-T2: un traspaso por falla no le escribe al cliente. Sólo el intento que falló; nada después del traspaso.</summary>
    [Fact]
    public async Task AFailureHandsOffWithoutSendingAnything()
    {
        var bed = new Bed(null, flow: HandoffAfterHttpFlow, http: new RecordingHttpBlocks(
            new HttpCallResult(false, null, RunVariables.Empty(), HttpCallOutcomes.ConnectionUnavailable, "secret_unreadable", "API de Clasic")));
        bed.Messenger.Reply = _ => new BotSendOutcome.Rejected("messaging.connection_unavailable", "secret_unreadable", "Servicio Clasic");

        await bed.Processor().ProcessAsync(Item, TestContext.Current.CancellationToken);

        Assert.Equal(["Te paso con un asesor."], bed.Messenger.Texts);
        Assert.Single(bed.Messenger.Sent);
        Assert.Equal(1, bed.Conversations.HandedOff);
    }

    /// <summary>Fila 5 (P2-R14): una salida apunta a un bloque que no existe en la versión.</summary>
    [Fact]
    public async Task AnEndRunTargetMissingSaysFlowFailed()
    {
        const string flow = """
            { "schema": 1, "start": { "firstNodeId": "n1" }, "variables": [],
              "nodes": [ { "id": "n1", "kind": "message", "title": "Hola", "text": "Hola", "outputs": { "next": "nadie" } } ] }
            """;
        var bed = new Bed(null, flow: flow);

        await bed.Processor().ProcessAsync(Item, TestContext.Current.CancellationToken);

        Assert.Equal(
            new HandoffCause(HandoffCauseKind.FlowFailed, "automations.run.target_missing", "n1", "Hola", null, null, null),
            Assert.Single(bed.Conversations.Causes));
        Assert.Equal("automations.run.target_missing", Assert.Single(bed.Runs.Added).FailureCode);
    }

    /// <summary>Spec 2026-10-25 D-4 (Review Focus 1): la regla se lee del chatbot en cada entrante, no de lo que la corrida guardó al
    /// arrancar. Una corrida que espera con dos intentos hechos y un chatbot que hoy dice «pasar a un asesor» termina traspasada.</summary>
    [Fact]
    public async Task AWaitingRunUsesTheRuleTheChatbotHasNow()
    {
        var memberId = Guid.CreateVersion7();
        var flow = """
            { "schema": 1, "start": { "firstNodeId": "n1" }, "variables": [],
              "nodes": [ { "id": "n1", "kind": "openQuestion", "title": "Documento", "text": "¿Cuál es tu documento?", "saveTo": "doc",
                "validation": { "kind": "regex", "pattern": "^[0-9]{6,10}$", "keywords": null }, "outputs": { "answered": null, "invalid": null } } ] }
            """;
        var bed = new Bed(
            null, ActiveState(), flow: flow,
            engineSettings: new EngineSettings(Chatbot.DefaultRestartKeyword, 3, AttemptsExhaustedAction.Handoff, memberId));
        var run = ConversationRun.Start(
            Guid.CreateVersion7(), TenantId, ConversationId, ConnectionId, ChatbotId, VersionId,
            RunState.Initial(RunVariables.Empty()) with { Status = RunStatus.Waiting, CurrentNodeId = "n1", Attempts = 2 }, DateTimeOffset.UtcNow.AddMinutes(-1));
        bed.Runs.Added.Add(run);

        await bed.Processor().ProcessAsync(Item, TestContext.Current.CancellationToken);

        Assert.Equal(RunStatus.HandedOff, run.Status);
        Assert.Equal(1, bed.Conversations.HandedOff);
        Assert.Equal(
            (new HandoffCause(HandoffCauseKind.AttemptsExhausted, null, "n1", "Documento", null, null, null), (Guid?)memberId),
            Assert.Single(bed.Conversations.Handoffs));
    }

    /// <summary>Fila 6 (P2-R6): la versión de la corrida viva ya no existe; no hay flujo del que sacar un bloque.</summary>
    [Fact]
    public async Task AMissingVersionSaysFlowFailedWithoutNode()
    {
        var bed = new Bed(null, ActiveState(), flow: null);
        var run = WaitingRun(DateTimeOffset.UtcNow.AddMinutes(-1));
        bed.Runs.Added.Add(run);

        await bed.Processor().ProcessAsync(Item, TestContext.Current.CancellationToken);

        Assert.Equal(
            new HandoffCause(HandoffCauseKind.FlowFailed, "automations.run.version_missing", null, null, null, null, null),
            Assert.Single(bed.Conversations.Causes));
        Assert.Equal((RunStatus.Failed, "automations.run.version_missing"), (run.Status, run.FailureCode));
    }

    /// <summary>Fila 7: una corrida <c>Running</c> en un bloque que no pide efecto externo (sólo con una versión corrupta).</summary>
    [Fact]
    public async Task ARunningRunWithoutPendingEffectSaysTargetMissing()
    {
        var bed = new Bed(null, ActiveState());
        var run = ConversationRun.Start(
            Guid.CreateVersion7(), TenantId, ConversationId, ConnectionId, ChatbotId, VersionId,
            RunState.Initial(RunVariables.Empty()) with { Status = RunStatus.Running, CurrentNodeId = "n1" }, DateTimeOffset.UtcNow.AddMinutes(-1));
        bed.Runs.Added.Add(run);

        await bed.Processor().ProcessAsync(Item, TestContext.Current.CancellationToken);

        Assert.Equal(
            new HandoffCause(HandoffCauseKind.FlowFailed, "automations.run.target_missing", "n1", "Hola", null, null, null),
            Assert.Single(bed.Conversations.Causes));
        Assert.Equal((RunStatus.Failed, "automations.run.target_missing"), (run.Status, run.FailureCode));
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

    /// <summary>
    /// Ruling P2-R42: con el módulo <c>automations</c> apagado el bot no contesta. La corrida viva termina <c>Interrupted</c> (con el
    /// entrante marcado, P2-R24), la conversación activa vuelve a None y la fila se cierra <c>skipped</c>, sin envíos ni auditoría.
    /// </summary>
    [Fact]
    public async Task WithTheModuleOffALiveRunIsInterruptedAndTheConversationEnds()
    {
        var bed = new Bed(
            startFailure: null,
            new AutomationConversationState("Open", null, "Active", ChatbotId, ConnectionId, DateTimeOffset.UtcNow, "Laura", "573001234567"),
            moduleEnabled: false);
        var run = WaitingRun(DateTimeOffset.UtcNow.AddMinutes(-1));
        bed.Runs.Added.Add(run);

        var outcome = await bed.Processor().ProcessAsync(Item, TestContext.Current.CancellationToken);

        Assert.Equal(RunOutcomes.Skipped, outcome);
        Assert.Equal([$"complete {Item.MessageId} 1 skipped"], bed.Inbox.Calls);
        Assert.Equal(RunStatus.Interrupted, run.Status);
        Assert.Equal(Item.MessageId, run.LastInboundMessageId);
        Assert.Equal(1, bed.Conversations.Ended);
        Assert.Single(bed.Runs.Added);
        Assert.Empty(bed.Messenger.Sent);
        Assert.Empty(bed.Audit.Actions);
    }

    /// <summary>P2-R42: sin corrida y sin asistente, el disparador ni se evalúa: nada arranca y la conversación no se toca.</summary>
    [Fact]
    public async Task WithTheModuleOffNoRunStarts()
    {
        var bed = new Bed(startFailure: null, moduleEnabled: false);

        var outcome = await bed.Processor().ProcessAsync(Item, TestContext.Current.CancellationToken);

        Assert.Equal(RunOutcomes.Skipped, outcome);
        Assert.Equal([$"complete {Item.MessageId} 1 skipped"], bed.Inbox.Calls);
        Assert.Empty(bed.Runs.Added);
        Assert.Empty(bed.Messenger.Sent);
        Assert.Equal((0, 0), (bed.Conversations.Started, bed.Conversations.Ended));
    }

    /// <summary>
    /// Ruling P2-R45: otra unidad tiene el lock de la conversación (la misma fila, reclamada otra vez porque la unidad original pasó su
    /// lease). Esta no espera detrás: reprograma la fila a 5 s con su testigo, no la cierra y no toca nada (ni la corrida ni la
    /// conversación ni el motor).
    /// </summary>
    [Fact]
    public async Task ABusyConversationLockRetriesTheRowInFiveSecondsWithoutAStep()
    {
        var bed = new Bed(startFailure: null, lockBusy: true);
        bed.Runs.Added.Add(WaitingRun(DateTimeOffset.UtcNow.AddMinutes(-1)));

        var outcome = await bed.Processor().ProcessAsync(Item, TestContext.Current.CancellationToken);

        Assert.Equal("locked", outcome);
        Assert.Equal([$"retry {Item.MessageId} 1 00:00:05"], bed.Inbox.Calls);
        Assert.Equal(0, bed.Runs.Reads);
        Assert.Equal(0, bed.Conversations.Reads);
        Assert.Single(bed.Runs.Added);
        Assert.Equal(RunStatus.Waiting, bed.Runs.Added[0].Status);
        Assert.Empty(bed.Messenger.Sent);
    }

    /// <summary>P2-R45: el barrido tampoco espera el lock; una unidad en curso es un entrante, y la corrida no está abandonada.</summary>
    [Fact]
    public async Task ABusyConversationLockSkipsTheAbandonCandidate()
    {
        var now = DateTimeOffset.UtcNow;
        var bed = new Bed(startFailure: null, lockBusy: true);
        var run = WaitingRun(now.AddHours(-25));
        bed.Runs.Added.Add(run);

        Assert.False(await bed.Processor().AbandonAsync(new AbandonedRun(TenantId, ConversationId, run.Id), now, TestContext.Current.CancellationToken));

        Assert.Equal(RunStatus.Waiting, run.Status);
        Assert.Equal(0, bed.Conversations.Ended);
    }

    /// <summary>
    /// Ruling P2-R43 y D-M8: el adjunto llegó antes de que <c>MediaCopyWorker</c> lo copiara (se guarda <c>stored: false</c>, sin url).
    /// En la unidad siguiente ya está en R2: antes del paso se marca <c>stored: true</c> y se firma la url, y el mensaje que la usa la lleva.
    /// </summary>
    [Fact]
    public async Task AnAttachmentStoredAfterItArrivedGetsItsUrlBeforeTheNextMessage()
    {
        var media = new StubMedia();
        var attachmentMessage = new InboundQueueItem(Guid.CreateVersion7(), TenantId, ConnectionId, ConversationId, Opened: false, DateTimeOffset.UtcNow, Attempts: 1);
        var reply = new InboundQueueItem(Guid.CreateVersion7(), TenantId, ConnectionId, ConversationId, Opened: false, DateTimeOffset.UtcNow, Attempts: 1);
        var bed = new Bed(startFailure: null, ActiveState(), flow: AttachmentFlow, media: media);
        bed.Inbox.Messages[attachmentMessage.MessageId] = new InboundMessage(
            attachmentMessage.MessageId, CustomerMessageKinds.Document, null, null, new InboundAttachment("application/pdf", "f.pdf", Stored: false), DateTimeOffset.UtcNow);
        var run = WaitingRun(DateTimeOffset.UtcNow.AddMinutes(-1), "n1");
        bed.Runs.Added.Add(run);

        await bed.Processor().ProcessAsync(attachmentMessage, TestContext.Current.CancellationToken);

        Assert.Equal("false|", $"{run.ToState().Variables.TextOf("factura.stored")}|{run.ToState().Variables.TextOf("factura.url")}");
        Assert.Equal(["¿Algo más?"], bed.Messenger.Texts);

        media.Stored[attachmentMessage.MessageId] = new StoredAttachment("tenant/media/f.pdf", "f.pdf", "application/pdf", 3);
        await bed.Processor().ProcessAsync(reply, TestContext.Current.CancellationToken);

        Assert.Equal(["¿Algo más?", "Tu factura: https://r2.test/tenant/media/f.pdf?signed=1"], bed.Messenger.Texts);
        var saved = run.ToState().Variables;
        Assert.Equal(("true", "https://r2.test/tenant/media/f.pdf?signed=1"), (saved.TextOf("factura.stored"), saved.TextOf("factura.url")));
        Assert.Equal(["tenant/media/f.pdf"], media.SignedKeys);
    }

    /// <summary>P2-R43: un envío no espera el adjunto. Si sigue sin copiarse, la url se interpola vacía y la variable queda como estaba.</summary>
    [Fact]
    public async Task AMessageDoesNotWaitForAnAttachmentStillNotStored()
    {
        var media = new StubMedia();
        var pending = Guid.CreateVersion7();
        var bed = new Bed(startFailure: null, ActiveState(), flow: AttachmentFlow, media: media);
        var run = WaitingRun(DateTimeOffset.UtcNow.AddMinutes(-1), "n2", PendingAttachment(pending));
        bed.Runs.Added.Add(run);

        await bed.Processor().ProcessAsync(Item, TestContext.Current.CancellationToken);

        Assert.Equal(["Tu factura: "], bed.Messenger.Texts);
        Assert.Equal("false", run.ToState().Variables.TextOf("factura.stored"));
        Assert.Equal([pending], media.Lookups);
        Assert.Empty(media.SignedKeys);
    }

    /// <summary>P2-R43: antes del bloque HTTP también se refresca, así el ejecutor recibe la url ya firmada y la corrida la guarda.</summary>
    [Fact]
    public async Task AnHttpBlockReceivesTheRefreshedAttachment()
    {
        const string flow = """
            { "schema": 1, "start": { "firstNodeId": "n1" }, "variables": [ { "name": "factura", "type": "attachment" } ],
              "nodes": [
                { "id": "n1", "kind": "attachment", "title": "Factura", "text": "Envía la factura", "accept": "any", "saveTo": "factura", "outputs": { "received": "n2", "invalid": null } },
                { "id": "n2", "kind": "openQuestion", "title": "Algo más", "text": "¿Algo más?", "saveTo": "nota", "validation": { "kind": "none" }, "outputs": { "answered": "n3", "invalid": null } },
                { "id": "n3", "kind": "http", "title": "Subir", "connectionId": "11111111-1111-1111-1111-111111111111", "method": "POST", "path": "/files", "body": { "kind": "json", "template": "{ \"url\": \"{factura.url}\" }" }, "mapping": [], "outputs": { "ok": null, "error": null } }
              ] }
            """;
        var media = new StubMedia();
        var stored = Guid.CreateVersion7();
        media.Stored[stored] = new StoredAttachment("tenant/media/f.pdf", "f.pdf", "application/pdf", 3);
        var http = new RecordingHttpBlocks(new HttpCallResult(true, 200, RunVariables.Empty(), HttpCallOutcomes.Ok, null));
        var bed = new Bed(startFailure: null, ActiveState(), flow: flow, http: http, media: media);
        bed.Runs.Added.Add(WaitingRun(DateTimeOffset.UtcNow.AddMinutes(-1), "n2", PendingAttachment(stored)));

        await bed.Processor().ProcessAsync(Item, TestContext.Current.CancellationToken);

        var (_, variables) = Assert.Single(http.Calls);
        Assert.Equal(("true", "https://r2.test/tenant/media/f.pdf?signed=1"), (variables.TextOf("factura.stored"), variables.TextOf("factura.url")));
    }

    /// <summary>P2-R46 (rama de AbandonAsync): bajo el lock la corrida viva ya no está <c>Waiting</c> (un entrante la dejó corriendo); no
    /// se abandona.</summary>
    [Fact]
    public async Task ARunNoLongerWaitingUnderTheLockIsNotAbandoned()
    {
        var now = DateTimeOffset.UtcNow;
        var bed = new Bed(startFailure: null, ActiveState());
        var run = ConversationRun.Start(
            Guid.CreateVersion7(), TenantId, ConversationId, ConnectionId, ChatbotId, VersionId,
            RunState.Initial(RunVariables.Empty()) with { Status = RunStatus.Running, CurrentNodeId = "n1" }, now.AddHours(-25));
        bed.Runs.Added.Add(run);

        Assert.False(await bed.Processor().AbandonAsync(new AbandonedRun(TenantId, ConversationId, run.Id), now, TestContext.Current.CancellationToken));

        Assert.Equal(RunStatus.Running, run.Status);
        Assert.Equal(0, bed.Conversations.Ended);
        Assert.Empty(bed.Audit.Actions);
    }

    /// <summary>P2-R46 (rama de AbandonAsync): la conversación se borró entre la lectura y <c>EndAsync</c>; no hay qué devolver a None,
    /// pero la corrida igual deja de esperar y queda la auditoría.</summary>
    [Fact]
    public async Task AConversationGoneAtEndStillAbandonsTheRun()
    {
        var now = DateTimeOffset.UtcNow;
        var bed = new Bed(
            startFailure: null, ActiveState(now.AddHours(-26)),
            endFailure: new ResourceNotFoundException("messaging.conversation.not_found", "La conversación no existe."));
        var run = WaitingRun(now.AddHours(-25));
        bed.Runs.Added.Add(run);

        Assert.True(await bed.Processor().AbandonAsync(new AbandonedRun(TenantId, ConversationId, run.Id), now, TestContext.Current.CancellationToken));

        Assert.Equal(RunStatus.Abandoned, run.Status);
        Assert.Equal([AutomationsAuditActions.RunAbandoned], bed.Audit.Actions);
    }

    // ---- Spec 2026-10-26: Política de datos por el puerto IAutomationDataConsents. ----

    /// <summary>Política de datos → un Mensaje por cada salida.</summary>
    private const string ConsentFlow = """
        { "schema": 1, "start": { "firstNodeId": "consent" }, "variables": [],
          "nodes": [
            { "id": "consent", "kind": "dataConsent", "title": "Política de datos", "text": "Lee https://e.co/d y dinos si aceptas.", "outputs": { "accepted": "ok", "rejected": "no" } },
            { "id": "ok", "kind": "message", "title": "Gracias", "text": "Gracias por aceptar.", "outputs": { "next": null } },
            { "id": "no", "kind": "message", "title": "Entendido", "text": "Entendido.", "outputs": { "next": null } }
          ] }
        """;

    /// <summary>D-2 y D-6: sin aceptación vigente pregunta con los botones fijos, espera, y recuerda el mensaje que salió.</summary>
    [Fact]
    public async Task TheConsentBlockAsksWhenThereIsNoAcceptance()
    {
        var bed = new Bed(null, flow: ConsentFlow, dataConsents: new RecordingDataConsents(DataConsentCheck.NotAccepted));

        await bed.Processor().ProcessAsync(Item, TestContext.Current.CancellationToken);

        var run = Assert.Single(bed.Runs.Added);
        var (message, messageId) = Assert.Single(bed.Messenger.Interactives);
        var buttons = Assert.IsType<InteractiveMessage.Buttons>(message);
        Assert.Equal([("accepted", "Acepto"), ("rejected", "No acepto")], buttons.Items);
        Assert.Equal(RunStatus.Waiting, run.Status);
        Assert.Equal(new ConsentPrompt("consent", "Lee https://e.co/d y dinos si aceptas.", messageId), run.ToState().ConsentPrompt);
        Assert.Equal([(TenantId, ConversationId)], bed.DataConsents.Checks);
        Assert.Empty(bed.Messenger.Texts);
    }

    /// <summary>D-2: con una aceptación vigente no pregunta y sigue por <c>accepted</c>.</summary>
    [Fact]
    public async Task ACurrentAcceptanceSkipsTheQuestion()
    {
        var bed = new Bed(null, flow: ConsentFlow, dataConsents: new RecordingDataConsents(DataConsentCheck.Accepted));

        await bed.Processor().ProcessAsync(Item, TestContext.Current.CancellationToken);

        Assert.Empty(bed.Messenger.Interactives);
        Assert.Equal(["Gracias por aceptar."], bed.Messenger.Texts);
        Assert.Empty(bed.DataConsents.Records);
    }

    /// <summary>D-7: sin cliente al que registrarle la respuesta no pregunta, sale por <c>rejected</c> y lo audita el sistema.</summary>
    [Fact]
    public async Task AnUnresolvableCustomerLeavesByRejectedAndAuditsTheSystem()
    {
        var bed = new Bed(null, flow: ConsentFlow, dataConsents: new RecordingDataConsents(DataConsentCheck.Unavailable));

        await bed.Processor().ProcessAsync(Item, TestContext.Current.CancellationToken);

        Assert.Empty(bed.Messenger.Interactives);
        Assert.Equal(["Entendido."], bed.Messenger.Texts);
        Assert.Contains((AutomationsAuditActions.DataConsentUnavailable, true), bed.Audit.Entries);
        Assert.Equal("automations.run.data_consent_unavailable", AutomationsAuditActions.DataConsentUnavailable);
        Assert.Empty(bed.DataConsents.Records);
    }

    /// <summary>D-5 y D-10: el toque se registra con la versión de la corrida, el texto que se mostró, el mensaje de la pregunta y el
    /// entrante que la respondió, antes de enviar el bloque siguiente.</summary>
    [Fact]
    public async Task ATapIsRecordedWithTheRunVersionAndTheReply()
    {
        var prompt = Guid.CreateVersion7();
        var bed = new Bed(null, ActiveState(), flow: ConsentFlow, dataConsents: new RecordingDataConsents(DataConsentCheck.NotAccepted));
        bed.Runs.Added.Add(ConsentRun(new ConsentPrompt("consent", "Texto", prompt)));
        bed.Inbox.Messages[Item.MessageId] = Tapped(Item.MessageId, "accepted");
        int? sentBeforeRecord = null;
        bed.DataConsents.OnRecord = () => sentBeforeRecord = bed.Messenger.Sent.Count;

        await bed.Processor().ProcessAsync(Item, TestContext.Current.CancellationToken);

        Assert.Equal(
            [new DataConsentRecord(
                TenantId, ConversationId, DataConsentDecision.Accepted, "Texto", prompt, Item.MessageId, ChatbotId, "Bienvenida", VersionId, 1, "consent")],
            bed.DataConsents.Records);
        Assert.Equal(0, sentBeforeRecord);
        Assert.Equal(["Gracias por aceptar."], bed.Messenger.Texts);
    }

    /// <summary>D-5: una respuesta que no se pudo registrar falla la corrida antes de seguir; el Mensaje de <c>accepted</c> no sale.</summary>
    [Fact]
    public async Task AReplyThatCannotBeRecordedFailsTheRunBeforeContinuing()
    {
        var bed = new Bed(null, ActiveState(), flow: ConsentFlow, dataConsents: new RecordingDataConsents(DataConsentCheck.NotAccepted, records: false));
        var run = ConsentRun(new ConsentPrompt("consent", "Texto", Guid.CreateVersion7()));
        bed.Runs.Added.Add(run);
        bed.Inbox.Messages[Item.MessageId] = Tapped(Item.MessageId, "accepted");

        await bed.Processor().ProcessAsync(Item, TestContext.Current.CancellationToken);

        Assert.Equal((RunStatus.Failed, "automations.run.data_consent_unrecorded"), (run.Status, run.FailureCode));
        Assert.Equal(AutomationsRunFailures.DataConsentUnrecorded, run.FailureCode);
        Assert.Equal(
            new HandoffCause(HandoffCauseKind.FlowFailed, "automations.run.data_consent_unrecorded", "consent", "Política de datos", null, null, null),
            Assert.Single(bed.Conversations.Causes));
        Assert.Empty(bed.Messenger.Sent);
    }

    /// <summary>Una excepción del puerto (un conflicto de concurrencia de Customers) no es «no se pudo registrar»: sube, la unidad se
    /// deshace y la fila se reintenta, sin fallar la corrida ni enviar el bloque siguiente.</summary>
    [Fact]
    public async Task ARecordThatThrowsLeavesTheUnitRetryable()
    {
        var dataConsents = new RecordingDataConsents(DataConsentCheck.NotAccepted)
        {
            Failure = new RequestConcurrencyException("concurrency.conflict", "El cliente cambió."),
        };
        var bed = new Bed(null, ActiveState(), flow: ConsentFlow, dataConsents: dataConsents);
        var run = ConsentRun(new ConsentPrompt("consent", "Texto", Guid.CreateVersion7()));
        bed.Runs.Added.Add(run);
        bed.Inbox.Messages[Item.MessageId] = Tapped(Item.MessageId, "accepted");

        var outcome = await bed.Processor().ProcessAsync(Item, TestContext.Current.CancellationToken);

        Assert.Equal(RunOutcomes.Retry, outcome);
        Assert.Equal([$"retry {Item.MessageId} 1 00:00:02"], bed.Inbox.Calls);
        Assert.Equal((RunStatus.Waiting, (string?)null), (run.Status, run.FailureCode));
        Assert.Empty(bed.Conversations.Handoffs);
        Assert.Empty(bed.Messenger.Sent);
    }

    /// <summary>D-4: agotar los intentos sale por <c>rejected</c> sin registrar nada, y queda la auditoría.</summary>
    [Fact]
    public async Task ExhaustingTheConsentAuditsAndRecordsNothing()
    {
        var bed = new Bed(null, ActiveState(), flow: ConsentFlow, dataConsents: new RecordingDataConsents(DataConsentCheck.NotAccepted));
        var run = ConsentRun(new ConsentPrompt("consent", "Texto", Guid.CreateVersion7()));
        bed.Runs.Added.Add(run);

        for (var reply = 0; reply < 3; reply++)
        {
            await bed.Processor().ProcessAsync(Item with { MessageId = Guid.CreateVersion7() }, TestContext.Current.CancellationToken);
        }

        Assert.Empty(bed.DataConsents.Records);
        Assert.Contains((AutomationsAuditActions.DataConsentUnanswered, true), bed.Audit.Entries);
        Assert.Equal("automations.run.data_consent_unanswered", AutomationsAuditActions.DataConsentUnanswered);
        Assert.Equal(["Entendido."], bed.Messenger.Texts);
        Assert.Equal(RunStatus.Completed, run.Status);
    }

    /// <summary>Una unidad que murió a mitad de la verificación deja la corrida <c>Running</c> en el bloque: se vuelve a preguntar.</summary>
    [Fact]
    public async Task ARunStuckOnTheConsentCheckAsksAgain()
    {
        var bed = new Bed(null, ActiveState(), flow: ConsentFlow, dataConsents: new RecordingDataConsents(DataConsentCheck.NotAccepted));
        var run = ConversationRun.Start(
            Guid.CreateVersion7(), TenantId, ConversationId, ConnectionId, ChatbotId, VersionId,
            RunState.Initial(RunVariables.Empty()) with { Status = RunStatus.Running, CurrentNodeId = "consent" }, DateTimeOffset.UtcNow.AddMinutes(-1));
        bed.Runs.Added.Add(run);

        await bed.Processor().ProcessAsync(Item, TestContext.Current.CancellationToken);

        Assert.Equal([(TenantId, ConversationId)], bed.DataConsents.Checks);
        Assert.Single(bed.Messenger.Interactives);
        Assert.Equal(RunStatus.Waiting, run.Status);
    }

    /// <summary>Spec 2026-10-26 repetir: Política de datos con un modo de «No acepto» y su miembro (sin salida <c>rejected</c>).</summary>
    private static string ConsentModeFlow(string onReject, Guid member) => $$"""
        { "schema": 1, "start": { "firstNodeId": "consent" }, "variables": [],
          "nodes": [
            { "id": "consent", "kind": "dataConsent", "title": "Política de datos", "text": "Lee https://e.co/d y dinos si aceptas.",
              "onReject": "{{onReject}}", "maxAsks": 1, "onRejectMemberId": "{{member}}", "outputs": { "accepted": "ok" } },
            { "id": "ok", "kind": "message", "title": "Gracias", "text": "Gracias por aceptar.", "outputs": { "next": null } }
          ] }
        """;

    /// <summary>Spec 2026-10-26 repetir R-4c: el «No acepto» al tope de <c>repeat</c> registra la fila y traspasa con la causa de la
    /// política de datos y el miembro del bloque.</summary>
    [Fact]
    public async Task ARejectionAtTheCapHandsOffWithTheConsentCause()
    {
        var member = Guid.CreateVersion7();
        var bed = new Bed(
            null, ActiveState(), flow: ConsentModeFlow("repeat", member), dataConsents: new RecordingDataConsents(DataConsentCheck.NotAccepted));
        var run = ConsentRun(new ConsentPrompt("consent", "Texto", Guid.CreateVersion7()));
        bed.Runs.Added.Add(run);
        bed.Inbox.Messages[Item.MessageId] = Tapped(Item.MessageId, "rejected");
        int? handoffsBeforeRecord = null;
        bed.DataConsents.OnRecord = () => handoffsBeforeRecord = bed.Conversations.Handoffs.Count;

        await bed.Processor().ProcessAsync(Item, TestContext.Current.CancellationToken);

        Assert.Equal(DataConsentDecision.Rejected, Assert.Single(bed.DataConsents.Records).Decision);
        Assert.Equal(0, handoffsBeforeRecord);
        Assert.Equal(
            [(new HandoffCause(HandoffCauseKind.DataConsentRejected, null, "consent", "Política de datos", null, null, null), (Guid?)member)],
            bed.Conversations.Handoffs);
        Assert.Equal(RunStatus.HandedOff, run.Status);
        Assert.Contains((AutomationsAuditActions.RunHandedOff, true), bed.Audit.Entries);
        Assert.Equal("automations.run.handed_off", AutomationsAuditActions.RunHandedOff);
    }

    /// <summary>Spec 2026-10-26 repetir R-4b: sin cliente al que registrarle la respuesta, <c>handoff</c> traspasa con la causa
    /// «no disponible» y el miembro del bloque, y queda la auditoría de sistema.</summary>
    [Fact]
    public async Task AnUnrecordableCustomerHandsOffWithTheUnavailableCause()
    {
        var member = Guid.CreateVersion7();
        var bed = new Bed(
            null, flow: ConsentModeFlow("handoff", member), dataConsents: new RecordingDataConsents(DataConsentCheck.Unavailable));

        await bed.Processor().ProcessAsync(Item, TestContext.Current.CancellationToken);

        Assert.Equal(
            [(new HandoffCause(HandoffCauseKind.DataConsentUnavailable, null, "consent", "Política de datos", null, null, null), (Guid?)member)],
            bed.Conversations.Handoffs);
        Assert.Contains((AutomationsAuditActions.DataConsentUnavailable, true), bed.Audit.Entries);
        Assert.Contains((AutomationsAuditActions.RunHandedOff, true), bed.Audit.Entries);
        Assert.Empty(bed.DataConsents.Records);
    }

    /// <summary>Spec 2026-10-26 repetir R-4: agotar las respuestas inválidas de una Política de datos en <c>repeat</c> traspasa como
    /// intentos agotados con el miembro del bloque, aunque la regla del chatbot sea reiniciar.</summary>
    [Fact]
    public async Task ExhaustedConsentHandsOffAsAttemptsExhausted()
    {
        var member = Guid.CreateVersion7();
        var bed = new Bed(
            null, ActiveState(), flow: ConsentModeFlow("repeat", member), dataConsents: new RecordingDataConsents(DataConsentCheck.NotAccepted),
            engineSettings: new EngineSettings(Chatbot.DefaultRestartKeyword, 3, AttemptsExhaustedAction.Restart));
        bed.Runs.Added.Add(ConsentRun(new ConsentPrompt("consent", "Texto", Guid.CreateVersion7())));

        for (var reply = 0; reply < 3; reply++)
        {
            await bed.Processor().ProcessAsync(Item with { MessageId = Guid.CreateVersion7() }, TestContext.Current.CancellationToken);
        }

        Assert.Equal(
            [(new HandoffCause(HandoffCauseKind.AttemptsExhausted, null, "consent", "Política de datos", null, null, null), (Guid?)member)],
            bed.Conversations.Handoffs);
        Assert.Contains((AutomationsAuditActions.DataConsentUnanswered, true), bed.Audit.Entries);
    }

    /// <summary>Spec 2026-10-26 repetir R-3: un «No acepto» que no se pudo registrar falla la corrida y no traspasa con la causa de la
    /// política de datos.</summary>
    [Fact]
    public async Task ARecordThatFailsDoesNotHandOff()
    {
        var bed = new Bed(
            null, ActiveState(), flow: ConsentModeFlow("handoff", Guid.CreateVersion7()),
            dataConsents: new RecordingDataConsents(DataConsentCheck.NotAccepted, records: false));
        var run = ConsentRun(new ConsentPrompt("consent", "Texto", Guid.CreateVersion7()));
        bed.Runs.Added.Add(run);
        bed.Inbox.Messages[Item.MessageId] = Tapped(Item.MessageId, "rejected");

        await bed.Processor().ProcessAsync(Item, TestContext.Current.CancellationToken);

        Assert.Equal((RunStatus.Failed, "automations.run.data_consent_unrecorded"), (run.Status, run.FailureCode));
        Assert.DoesNotContain(bed.Conversations.Handoffs, handoff => handoff.Cause.Kind == HandoffCauseKind.DataConsentRejected);
    }

    private static ConversationRun ConsentRun(ConsentPrompt prompt) =>
        ConversationRun.Start(
            Guid.CreateVersion7(), TenantId, ConversationId, ConnectionId, ChatbotId, VersionId,
            RunState.Initial(RunVariables.Empty()) with { Status = RunStatus.Waiting, CurrentNodeId = "consent", ConsentPrompt = prompt },
            DateTimeOffset.UtcNow.AddMinutes(-1));

    private static InboundMessage Tapped(Guid messageId, string replyId) =>
        new(messageId, CustomerMessageKinds.Interactive, replyId == "accepted" ? "Acepto" : "No acepto", replyId, null, DateTimeOffset.UtcNow);

    /// <summary>Pedir adjunto → pregunta → mensaje que usa la url del adjunto (D-M8).</summary>
    private const string AttachmentFlow = """
        { "schema": 1, "start": { "firstNodeId": "n1" }, "variables": [ { "name": "factura", "type": "attachment" } ],
          "nodes": [
            { "id": "n1", "kind": "attachment", "title": "Factura", "text": "Envía la factura", "accept": "any", "saveTo": "factura", "outputs": { "received": "n2", "invalid": null } },
            { "id": "n2", "kind": "openQuestion", "title": "Algo más", "text": "¿Algo más?", "saveTo": "nota", "validation": { "kind": "none" }, "outputs": { "answered": "n3", "invalid": null } },
            { "id": "n3", "kind": "message", "title": "Factura", "text": "Tu factura: {factura.url}", "outputs": { "next": null } }
          ] }
        """;

    private static AutomationConversationState ActiveState(DateTimeOffset? lastInboundAt = null) =>
        new("Open", null, "Active", ChatbotId, ConnectionId, lastInboundAt ?? DateTimeOffset.UtcNow, "Laura", "573001234567");

    /// <summary>Lo que guarda el bloque Adjunto (CustomerReply.Receive) de un archivo que todavía no está en R2.</summary>
    private static RunVariables PendingAttachment(Guid messageId)
    {
        var variables = RunVariables.Empty();
        variables.Set("factura", new System.Text.Json.Nodes.JsonObject
        {
            ["messageId"] = messageId.ToString(), ["fileName"] = "f.pdf", ["mimeType"] = "application/pdf", ["stored"] = false,
        });
        return variables;
    }

    private static ConversationRun WaitingRun(DateTimeOffset updatedAt, string nodeId = "n1", RunVariables? variables = null) =>
        ConversationRun.Start(
            Guid.CreateVersion7(), TenantId, ConversationId, ConnectionId, ChatbotId, VersionId,
            RunState.Initial(variables ?? RunVariables.Empty()) with { Status = RunStatus.Waiting, CurrentNodeId = nodeId }, updatedAt);

    private sealed class TestDomainException() : DomainException("messaging.automation.not_available", "Ya no es del bot.");

    private sealed class Bed(
        Exception? startFailure, AutomationConversationState? state = null, IClock? clock = null, Action? onLock = null, string? flow = Flow,
        IHttpBlockExecutor? http = null, bool moduleEnabled = true, bool lockBusy = false, Exception? endFailure = null, IAutomationMedia? media = null,
        EngineSettings? engineSettings = null, RecordingDataConsents? dataConsents = null)
    {
        public RecordingInbox Inbox { get; } = new();

        public RecordingDataConsents DataConsents { get; } = dataConsents ?? new(DataConsentCheck.NotAccepted);

        public MemoryRuns Runs { get; } = new();

        public RecordingMessenger Messenger { get; } = new();

        public Conversations Conversations { get; } = new(startFailure, state, endFailure);

        public RecordingAudit Audit { get; } = new();

        public RunProcessor Processor()
        {
            var conversations = Conversations;
            var audit = Audit;
            var hours = new OpenHours();
            var files = media ?? new NoMedia();
            var effects = new RunEffects(
                Messenger, conversations, hours, http ?? new RecordingHttpBlocks(null), files, audit, DataConsents, new ServingChatbots(flow, engineSettings),
                NullLogger<RunEffects>.Instance);
            return new RunProcessor(
                new NoUnitOfWork(onLock, lockBusy), Runs, new ServingChatbots(flow, engineSettings), Inbox, conversations, new TenantNames(), new Connections(), hours,
                files, new ModuleStatus(moduleEnabled), audit, effects, clock ?? new FixedClock(), NullLogger<RunProcessor>.Instance);
        }
    }

    /// <param name="busy">Ruling P2-R45: otra unidad tiene el lock de la conversación.</param>
    private sealed class NoUnitOfWork(Action? onLock, bool busy) : IAutomationsUnitOfWork, IAsyncDisposable
    {
        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken cancellationToken) => Task.FromResult<IAsyncDisposable>(this);

        public Task<bool> TryLockConversationAsync(Guid conversationId, CancellationToken cancellationToken)
        {
            onLock?.Invoke();
            return Task.FromResult(!busy);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MemoryRuns : IConversationRunRepository
    {
        public List<ConversationRun> Added { get; } = [];

        public int Reads { get; private set; }

        public Task<ConversationRun?> FindLiveForUpdateAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(Added.FirstOrDefault(run => run.IsLive));
        }

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

    /// <param name="flow"><c>null</c>: la versión de la corrida ya no existe (P2-R6).</param>
    private sealed class ServingChatbots(string? flow, EngineSettings? settings = null) : IChatbotRepository
    {
        public Task<ServingChatbot?> FindServingByConnectionAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
            Task.FromResult<ServingChatbot?>(new ServingChatbot(
                ChatbotId, "Servicio", ChatbotStatus.Active, ChatbotTrigger.NewConversation, [], Chatbot.DefaultRestartKeyword, 3, VersionId));

        public Task<string?> GetVersionFlowAsync(Guid tenantId, Guid versionId, CancellationToken cancellationToken) => Task.FromResult(flow);

        public Task<EngineSettings?> FindEngineSettingsAsync(Guid tenantId, Guid chatbotId, CancellationToken cancellationToken) =>
            Task.FromResult<EngineSettings?>(settings ?? new EngineSettings(Chatbot.DefaultRestartKeyword, 3));

        public Task<ChatbotVersionLabel?> FindVersionLabelAsync(Guid tenantId, Guid versionId, CancellationToken cancellationToken) =>
            Task.FromResult<ChatbotVersionLabel?>(tenantId == TenantId && versionId == VersionId ? new ChatbotVersionLabel("Bienvenida", 1) : null);

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

        /// <summary>Los entrantes que la prueba fija por id; cualquier otro es un texto «Hola».</summary>
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

    private sealed class Conversations(Exception? startFailure, AutomationConversationState? state, Exception? endFailure) : IAutomationConversations
    {
        public int Ended { get; private set; }

        public int HandedOff { get; private set; }

        /// <summary>Spec 2026-10-24: la causa de cada traspaso, en orden.</summary>
        public List<HandoffCause> Causes => [.. Handoffs.Select(handoff => handoff.Cause)];

        /// <summary>Spec 2026-10-25 D-1: la causa y el miembro al que se pidió asignar, en orden.</summary>
        public List<(HandoffCause Cause, Guid? Member)> Handoffs { get; } = [];

        public int Started { get; private set; }

        public int Reads { get; private set; }

        public Task<AutomationConversationState?> GetAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult<AutomationConversationState?>(state ?? new AutomationConversationState(
                "Open", null, "None", null, ConnectionId, DateTimeOffset.UtcNow, "Laura", "573001234567"));
        }

        public Task StartAsync(Guid tenantId, Guid conversationId, Guid chatbotId, string? firstStepTitle, CancellationToken cancellationToken)
        {
            Started++;
            return startFailure is null ? Task.CompletedTask : Task.FromException(startFailure);
        }

        public Task SetStepAsync(Guid tenantId, Guid conversationId, string? stepTitle, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetChatbotAsync(Guid tenantId, Guid conversationId, Guid chatbotId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task HandoffAsync(Guid tenantId, Guid conversationId, HandoffCause cause, Guid? assignToMemberId, CancellationToken cancellationToken)
        {
            HandedOff++;
            Handoffs.Add((cause, assignToMemberId));
            return Task.CompletedTask;
        }

        public Task ResolveAsync(Guid tenantId, Guid conversationId, Guid? reasonId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task EndAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken)
        {
            if (endFailure is not null)
            {
                return Task.FromException(endFailure);
            }

            Ended++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingMessenger : IAutomationMessenger
    {
        public List<Guid> Sent { get; } = [];

        public List<string> Texts { get; } = [];

        /// <summary>Cada interactivo enviado con el id de mensaje que Mensajería devolvió.</summary>
        public List<(InteractiveMessage Message, Guid MessageId)> Interactives { get; } = [];

        /// <summary>Cómo responde Mensajería a cada texto; sin respuesta, lo da por enviado.</summary>
        public Func<string, BotSendOutcome>? Reply { get; set; }

        public Task<BotSendOutcome> SendTextAsync(Guid tenantId, Guid conversationId, Guid chatbotId, Guid clientId, string text, CancellationToken cancellationToken)
        {
            Sent.Add(clientId);
            Texts.Add(text);
            return Task.FromResult(Reply?.Invoke(text) ?? new BotSendOutcome.Sent(Guid.CreateVersion7()));
        }

        public Task<BotSendOutcome> SendInteractiveAsync(
            Guid tenantId, Guid conversationId, Guid chatbotId, Guid clientId, InteractiveMessage message, CancellationToken cancellationToken)
        {
            Sent.Add(clientId);
            var messageId = Guid.CreateVersion7();
            Interactives.Add((message, messageId));
            return Task.FromResult<BotSendOutcome>(new BotSendOutcome.Sent(messageId));
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

    /// <summary>R2 de mentira: los adjuntos que la prueba da por copiados, y cada llave que se firmó.</summary>
    private sealed class StubMedia : IAutomationMedia
    {
        public Dictionary<Guid, StoredAttachment> Stored { get; } = [];

        public List<Guid> Lookups { get; } = [];

        public List<string> SignedKeys { get; } = [];

        public Task<StoredAttachment?> TryGetStoredAsync(Guid tenantId, Guid messageId, CancellationToken cancellationToken)
        {
            Lookups.Add(messageId);
            return Task.FromResult(Stored.TryGetValue(messageId, out var stored) ? stored : null);
        }

        public Task<Uri> CreateDownloadUrlAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken)
        {
            Assert.Equal(TimeSpan.FromMinutes(15), lifetime);
            SignedKeys.Add(key);
            return Task.FromResult(new Uri($"https://r2.test/{key}?signed=1"));
        }

        public Task<(Stream Content, long Length)?> OpenReadAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class ModuleStatus(bool enabled) : IAutomationsModuleStatus
    {
        public Task<bool> IsEnabledAsync(Guid tenantId, CancellationToken cancellationToken) => Task.FromResult(enabled);
    }

    private sealed class RecordingAudit : IAutomationsAuditRecorder
    {
        public List<(string Action, bool BySystem)> Entries { get; } = [];

        public List<string> Actions => [.. Entries.Select(entry => entry.Action)];

        public void Record(Guid tenantId, Guid actorId, bool bySystem, string action, string resourceType, Guid resourceId, DateTimeOffset at) =>
            Entries.Add((action, bySystem));
    }

    /// <summary>Spec 2026-10-26: el registro de Customers de mentira. Responde <paramref name="check"/> a cada verificación y
    /// <paramref name="records"/> a cada registro; con <see cref="Failure"/>, el registro lanza como lo haría el adaptador.</summary>
    private sealed class RecordingDataConsents(DataConsentCheck check, bool records = true) : IAutomationDataConsents
    {
        public List<(Guid TenantId, Guid ConversationId)> Checks { get; } = [];

        public List<DataConsentRecord> Records { get; } = [];

        public Exception? Failure { get; init; }

        /// <summary>Corre al registrar, para que la prueba mire qué había salido hasta ese momento.</summary>
        public Action? OnRecord { get; set; }

        public Task<DataConsentCheck> CheckAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken)
        {
            Checks.Add((tenantId, conversationId));
            return Task.FromResult(check);
        }

        public Task<bool> RecordAsync(DataConsentRecord record, CancellationToken cancellationToken)
        {
            OnRecord?.Invoke();
            if (Failure is not null)
            {
                return Task.FromException<bool>(Failure);
            }

            if (records)
            {
                Records.Add(record);
            }

            return Task.FromResult(records);
        }
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
