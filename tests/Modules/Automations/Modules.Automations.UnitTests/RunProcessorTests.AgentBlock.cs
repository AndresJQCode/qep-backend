using System.Text.Json;
using Modules.Automations.Application;
using Modules.Automations.Application.Engine;
using Modules.Automations.Domain;
using Modules.Automations.Domain.Engine;

namespace Modules.Automations.UnitTests;

/// <summary>Spec 2026-11-17 (agente IA en chatbots) §6.1, §6.3, D-B14 y D-B16: el procesador delega en la misma unidad y retoma la corrida con
/// la fila <c>AgentReturned</c>.</summary>
public sealed partial class RunProcessorTests
{
    private static readonly Guid BlockAgentId = Guid.Parse("a1a1a1a1-a1a1-a1a1-a1a1-a1a1a1a1a1a1");

    private const string AgentFlow = """
        { "schema": 1, "start": { "firstNodeId": "ai" }, "variables": [],
          "nodes": [
            { "id": "ai", "kind": "aiAgent", "title": "Asesor IA", "agentId": "a1a1a1a1-a1a1-a1a1-a1a1-a1a1a1a1a1a1",
              "objective": "Resuelve dudas.", "context": "Cliente {nombre_contacto}", "firstTurn": "lastMessage", "idleMinutes": 45, "saveTo": "asesor",
              "outputs": { "done": "bye", "human": "bye", "noAnswer": "bye", "idle": "bye", "error": "oops" } },
            { "id": "bye", "kind": "message", "title": "Despedida", "text": "Listo: {asesor.summary}", "outputs": { "next": null } },
            { "id": "oops", "kind": "message", "title": "Falla", "text": "No pudimos ({asesor.reason})", "outputs": { "next": null } }
          ] }
        """;

    private static InboundQueueItem ReturnedItem(Guid delegationId) =>
        new(delegationId, TenantId, ConnectionId, ConversationId, Opened: false, DateTimeOffset.UtcNow, Attempts: 1, DelegationId: delegationId);

    private static ConversationRun DelegatedRun(DateTimeOffset startedAt)
    {
        var state = RunState.Initial(RunVariables.Empty()) with
        {
            Status = RunStatus.Waiting,
            CurrentNodeId = "ai",
            StepNumber = 1,
            Trace = [new TraceEntry(startedAt, "ai", "aiAgent", TraceOutcomes.Delegated)],
        };
        return ConversationRun.Start(Guid.CreateVersion7(), TenantId, ConversationId, ConnectionId, ChatbotId, VersionId, state, startedAt);
    }

    private static DelegationResult Returned(ConversationRun run, string json, string status = DelegationResult.Returned) =>
        new(Guid.CreateVersion7(), ConversationId, ChatbotId, run.Id, "ai", BlockAgentId, status, json);

    [Fact]
    public async Task TheDelegationRunsInTheSameUnitAndTheRunWaits()
    {
        var bed = new Bed(null, flow: AgentFlow);

        var outcome = await bed.Processor().ProcessAsync(Item, TestContext.Current.CancellationToken);

        var run = Assert.Single(bed.Runs.Added);
        Assert.Equal(RunOutcomes.Started, outcome);
        Assert.True(run.ToState().IsDelegated);
        Assert.Empty(bed.Messenger.Sent);
        var request = Assert.Single(bed.Conversations.Delegations);
        Assert.Equal((run.Id, ChatbotId, "ai", BlockAgentId, (Guid?)Item.MessageId), (request.RunId, request.ChatbotId, request.NodeId, request.AgentId, request.LastInboundMessageId));
        using var brief = JsonDocument.Parse(request.BriefJson);
        Assert.Equal("Resuelve dudas.", brief.RootElement.GetProperty("objective").GetString());
        Assert.Equal("Cliente Laura", brief.RootElement.GetProperty("context").GetString());
        Assert.Equal("lastMessage", brief.RootElement.GetProperty("firstTurn").GetString());
        Assert.Equal(45, brief.RootElement.GetProperty("idleMinutes").GetInt32());
    }

    // §6.1 paso 2: una persona la tomó entre el claim y el efecto; Mensajería lanza su excepción de dominio.
    [Fact]
    public async Task ADelegationTheConversationLostInterruptsTheRun()
    {
        var bed = new Bed(null, flow: AgentFlow);
        bed.Conversations.DelegateFailure = new TestDomainException();

        var outcome = await bed.Processor().ProcessAsync(Item, TestContext.Current.CancellationToken);

        Assert.Equal(RunOutcomes.Interrupted, outcome);
        Assert.Equal(RunStatus.Interrupted, Assert.Single(bed.Runs.Added).Status);
        Assert.Empty(bed.Conversations.Handoffs);
    }

    [Fact]
    public async Task AnAgentReturnedRowResumesTheRunThroughItsOutput()
    {
        var run = DelegatedRun(DateTimeOffset.UtcNow.AddMinutes(-10));
        var bed = new Bed(null, ActiveState(), flow: AgentFlow);
        bed.Runs.Added.Add(run);
        var delegation = Returned(run, """{"output":"done","reason":"objective_completed","summary":"Quiere la talla M"}""");
        bed.Conversations.DelegationResults[delegation.DelegationId] = delegation;
        var item = ReturnedItem(delegation.DelegationId);
        bed.Inbox.NotInbound.Add(item.MessageId);

        var outcome = await bed.Processor().ProcessAsync(item, TestContext.Current.CancellationToken);

        Assert.Equal(RunOutcomes.Ended, outcome);
        Assert.Equal(["Listo: Quiere la talla M"], bed.Messenger.Texts);
        Assert.Equal((RunStatus.Completed, (Guid?)item.MessageId), (run.Status, run.LastInboundMessageId));
        Assert.Equal([$"complete {item.MessageId} 1 ended"], bed.Inbox.Calls);
    }

    [Fact]
    public async Task AQuotationDraftWithoutHumanPathGoesToTheAdvisor()
    {
        const string flow = """
            { "schema": 1, "start": { "firstNodeId": "ai" }, "variables": [],
              "nodes": [ { "id": "ai", "kind": "aiAgent", "title": "Asesor IA", "agentId": "a1a1a1a1-a1a1-a1a1-a1a1-a1a1a1a1a1a1",
                "objective": "Cotiza.", "context": "", "saveTo": "asesor", "outputs": { "done": null, "human": null, "noAnswer": null, "idle": null, "error": null } } ] }
            """;
        var advisor = Guid.CreateVersion7();
        var run = DelegatedRun(DateTimeOffset.UtcNow.AddMinutes(-10));
        var bed = new Bed(null, ActiveState(), flow: flow);
        bed.Runs.Added.Add(run);
        var delegation = Returned(
            run, $$"""{"output":"human","reason":"quotation_draft_created","quotationNumber":"COT-7","advisorMemberId":"{{advisor}}"}""");
        bed.Conversations.DelegationResults[delegation.DelegationId] = delegation;

        await bed.Processor().ProcessAsync(ReturnedItem(delegation.DelegationId), TestContext.Current.CancellationToken);

        var (cause, member) = Assert.Single(bed.Conversations.Handoffs);
        Assert.Equal((HandoffCauseKind.AgentBlockOutputUnhandled, "human", advisor), (cause.Kind, cause.Code, member));
        Assert.Equal((ChatbotId, BlockAgentId), (cause.ChatbotId!.Value, cause.AgentId!.Value));
        Assert.Equal(RunStatus.HandedOff, run.Status);
        Assert.Equal("COT-7", run.ToState().Variables.TextOf("asesor.quotationNumber"));
    }

    // §6.6: la segunda fila (reintento) ya no encuentra la corrida delegada en ese bloque.
    [Fact]
    public async Task ADuplicateAgentReturnedRowIsSkipped()
    {
        var run = DelegatedRun(DateTimeOffset.UtcNow.AddMinutes(-10));
        var bed = new Bed(null, ActiveState(), flow: AgentFlow);
        bed.Runs.Added.Add(run);
        var delegation = Returned(run, """{"output":"done","reason":"objective_completed"}""");
        bed.Conversations.DelegationResults[delegation.DelegationId] = delegation;
        var processor = bed.Processor();
        await processor.ProcessAsync(ReturnedItem(delegation.DelegationId), TestContext.Current.CancellationToken);
        var other = DelegatedRun(DateTimeOffset.UtcNow.AddMinutes(-5));
        bed.Runs.Added.Add(other);
        var sent = bed.Messenger.Sent.Count;

        // Otra corrida delegada viva no es la de esta delegación: tampoco se mueve.
        var outcome = await processor.ProcessAsync(ReturnedItem(delegation.DelegationId) with { MessageId = Guid.CreateVersion7() }, TestContext.Current.CancellationToken);

        Assert.Equal(RunOutcomes.Skipped, outcome);
        Assert.Equal(sent, bed.Messenger.Sent.Count);
        Assert.True(other.ToState().IsDelegated);
    }

    [Fact]
    public async Task AnInterruptedDelegationIsSkipped()
    {
        var run = DelegatedRun(DateTimeOffset.UtcNow.AddMinutes(-10));
        var bed = new Bed(null, ActiveState(), flow: AgentFlow);
        bed.Runs.Added.Add(run);
        var delegation = Returned(run, """{"output":"done","reason":"x"}""", DelegationResult.Interrupted);
        bed.Conversations.DelegationResults[delegation.DelegationId] = delegation;

        var outcome = await bed.Processor().ProcessAsync(ReturnedItem(delegation.DelegationId), TestContext.Current.CancellationToken);

        Assert.Equal(RunOutcomes.Skipped, outcome);
        Assert.True(run.ToState().IsDelegated);
    }

    // §6.4: con la ventana cerrada el agente vuelve sin salida; la corrida termina y el asistente se apaga.
    [Fact]
    public async Task AReturnWithoutOutputClosesTheRunWithTheWindow()
    {
        var run = DelegatedRun(DateTimeOffset.UtcNow.AddMinutes(-10));
        var bed = new Bed(null, ActiveState(), flow: AgentFlow);
        bed.Runs.Added.Add(run);
        var delegation = Returned(run, """{"output":null,"reason":"window_closed"}""");
        bed.Conversations.DelegationResults[delegation.DelegationId] = delegation;

        await bed.Processor().ProcessAsync(ReturnedItem(delegation.DelegationId), TestContext.Current.CancellationToken);

        Assert.Equal(RunStatus.WindowClosed, run.Status);
        Assert.Equal(1, bed.Conversations.Ended);
        Assert.Empty(bed.Messenger.Sent);
        Assert.Empty(bed.Conversations.Handoffs);
    }

    // D-B16: sin el módulo ai el bloque sale por error sin delegar.
    [Fact]
    public async Task WithTheAiModuleOffTheBlockLeavesThroughError()
    {
        var bed = new Bed(null, flow: AgentFlow, aiEnabled: false);

        await bed.Processor().ProcessAsync(Item, TestContext.Current.CancellationToken);

        Assert.Empty(bed.Conversations.Delegations);
        Assert.Equal(["No pudimos (module_disabled)"], bed.Messenger.Texts);
    }

    // §6.5: mientras el agente conversa la corrida no se mueve; la inactividad la decide el bloque (D-B11), no el barrido de 24 h.
    [Fact]
    public async Task ADelegatedRunIsNeverAbandoned()
    {
        var now = DateTimeOffset.UtcNow;
        var run = DelegatedRun(now.AddHours(-30));
        var bed = new Bed(null, ActiveState(), flow: AgentFlow);
        bed.Runs.Added.Add(run);

        var abandoned = await bed.Processor().AbandonAsync(
            new AbandonedRun(TenantId, ConversationId, run.Id), now, TestContext.Current.CancellationToken);

        Assert.False(abandoned);
        Assert.True(run.ToState().IsDelegated);
        Assert.Equal(0, bed.Conversations.Ended);
        Assert.Empty(bed.Audit.Actions);
    }

    // Revisión de la rama, hallazgo 1: un entrante que quedó en la cola del chatbot mientras la corrida espera al agente no es una respuesta
    // del bloque. Entrarlo al motor fallaba con target_missing y el traspaso borraba la delegación sin avisarle al agente.
    [Fact]
    public async Task ACustomerMessageForADelegatedRunIsSkippedAndNeverFailsTheFlow()
    {
        var run = DelegatedRun(DateTimeOffset.UtcNow.AddMinutes(-10));
        var bed = new Bed(null, ActiveState(), flow: AgentFlow);
        bed.Runs.Added.Add(run);

        var outcome = await bed.Processor().ProcessAsync(Item, TestContext.Current.CancellationToken);

        Assert.Equal(RunOutcomes.Skipped, outcome);
        Assert.True(run.ToState().IsDelegated);
        Assert.Empty(bed.Conversations.Handoffs);
        Assert.Empty(bed.Messenger.Sent);
        Assert.Equal([$"complete {Item.MessageId} 1 skipped"], bed.Inbox.Calls);
    }

    // Hallazgo 1: Mensajería mueve a la cola del agente los entrantes pendientes de la conversación, menos la fila que esta unidad procesa;
    // ésa es la que la delegación nombra, también en un arranque manual sin entrante.
    [Fact]
    public async Task TheDelegationNamesTheInboxRowBeingProcessed()
    {
        var bed = new Bed(null, ActiveState(), flow: AgentFlow);
        var manual = Item with { MessageId = Guid.CreateVersion7(), Opened = false };
        bed.Inbox.NotInbound.Add(manual.MessageId);

        await bed.Processor().ProcessAsync(manual, TestContext.Current.CancellationToken);

        var request = Assert.Single(bed.Conversations.Delegations);
        Assert.Equal(((Guid?)null, (Guid?)manual.MessageId), (request.LastInboundMessageId, request.InboxRowId));
    }

    // D-B14: la señal de la toma humana (bot_kind = 1) cierra la corrida delegada sin salir por ninguna salida.
    [Fact]
    public async Task TakingOverDuringTheDelegationInterruptsTheRunWithoutAnyOutput()
    {
        var created = DateTimeOffset.UtcNow;
        var run = DelegatedRun(created.AddMinutes(-10));
        var bed = new Bed(null, PersonState("took", run.StartedAt), flow: AgentFlow);
        bed.Runs.Added.Add(run);
        var item = ReleaseItem(created);
        bed.Inbox.NotInbound.Add(item.MessageId);

        var outcome = await bed.Processor().ProcessAsync(item, TestContext.Current.CancellationToken);

        Assert.Equal(RunOutcomes.Interrupted, outcome);
        Assert.Equal(RunStatus.Interrupted, run.Status);
        Assert.Empty(bed.Messenger.Sent);
        Assert.Empty(bed.Conversations.Handoffs);
    }
}
