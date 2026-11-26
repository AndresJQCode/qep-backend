using BuildingBlocks.Application;
using Modules.Automations.Application;
using Modules.Automations.Application.Engine;
using Modules.Automations.Domain;
using Modules.Automations.Domain.Engine;
using Modules.Automations.Domain.Flow;

namespace Modules.Automations.UnitTests;

/// <summary>
/// Spec 2026-11-19 (chatbot CRM action): the «Acción de CRM» effect through <see cref="IAutomationCrm"/>. The port's answer comes back
/// to the engine as <see cref="EngineInput.CrmResult"/> in the same unit; anything the port throws that is not «not yet» is
/// <c>failed</c> (P2-R33a: nothing escapes the unit), and the Automations audit records what was applied, skipped or failed.
/// </summary>
public sealed partial class RunProcessorTests
{
    private const string CrmTagId = "77777777-7777-7777-7777-777777777777";

    /// <summary>«Acción de CRM» (add a tag) → one Mensaje per output, each one naming the result code.</summary>
    private const string CrmFlow = $$"""
        { "schema": 1, "start": { "firstNodeId": "crm" }, "variables": [],
          "nodes": [
            { "id": "crm", "kind": "crmAction", "title": "Etiquetar", "operation": "addTag", "tagId": "{{CrmTagId}}", "stageId": null,
              "saveTo": "crm", "outputs": { "ok": "ok", "skipped": "skipped", "error": "error" } },
            { "id": "ok", "kind": "message", "title": "Listo", "text": "ok {crm.result}", "outputs": { "next": null } },
            { "id": "skipped", "kind": "message", "title": "Nada", "text": "skipped {crm.result}", "outputs": { "next": null } },
            { "id": "error", "kind": "message", "title": "Error", "text": "error {crm.result}", "outputs": { "next": null } }
          ] }
        """;

    [Theory]
    [InlineData(CrmResultCodes.Added, "ok added", AutomationsAuditActions.CrmActionApplied)]
    [InlineData(CrmResultCodes.NoCustomer, "skipped no_customer", AutomationsAuditActions.CrmActionSkipped)]
    [InlineData(CrmResultCodes.TagLimit, "error tag_limit", AutomationsAuditActions.CrmActionFailed)]
    public async Task ExecutesTheOperationAndLeavesByTheMappedOutput(string code, string text, string audited)
    {
        var crm = new RecordingCrm(code);
        var bed = new Bed(null, flow: CrmFlow, crm: crm);

        await bed.Processor().ProcessAsync(Item, TestContext.Current.CancellationToken);

        var run = Assert.Single(bed.Runs.Added);
        Assert.Equal([$"addTag {TenantId} {ConversationId} {CrmTagId}"], crm.Calls);
        Assert.Equal([text], bed.Messenger.Texts);
        Assert.Equal(code, run.ToState().Variables.TextOf("crm.result"));
        Assert.Equal([AutomationsAuditActions.RunStarted, audited, AutomationsAuditActions.RunCompleted], bed.Audit.Actions);
        Assert.All(bed.Audit.Entries, entry => Assert.True(entry.BySystem));
    }

    /// <summary>Idempotent no-ops leave by <c>ok</c> without an audit row: nothing changed.</summary>
    [Theory]
    [InlineData(CrmResultCodes.AlreadyTagged)]
    [InlineData(CrmResultCodes.NotTagged)]
    [InlineData(CrmResultCodes.AlreadyInStage)]
    public async Task AnIdempotentNoOpLeavesByOkWithoutAnAuditRow(string code)
    {
        var bed = new Bed(null, flow: CrmFlow, crm: new RecordingCrm(code));

        await bed.Processor().ProcessAsync(Item, TestContext.Current.CancellationToken);

        Assert.Equal([$"ok {code}"], bed.Messenger.Texts);
        Assert.Equal([AutomationsAuditActions.RunStarted, AutomationsAuditActions.RunCompleted], bed.Audit.Actions);
    }

    [Fact]
    public async Task EachOperationCallsItsPortMethod()
    {
        const string flow = """
            { "schema": 1, "start": { "firstNodeId": "a" }, "variables": [],
              "nodes": [
                { "id": "a", "kind": "crmAction", "title": "Quitar", "operation": "removeTag", "tagId": "77777777-7777-7777-7777-777777777777",
                  "stageId": null, "saveTo": "crm", "outputs": { "ok": "b", "skipped": null, "error": null } },
                { "id": "b", "kind": "crmAction", "title": "Mover", "operation": "moveDeal", "tagId": null,
                  "stageId": "88888888-8888-8888-8888-888888888888", "saveTo": "crm2", "outputs": { "ok": null, "skipped": null, "error": null } }
              ] }
            """;
        var crm = new RecordingCrm(CrmResultCodes.Removed, CrmResultCodes.Moved);
        var bed = new Bed(null, flow: flow, crm: crm);

        await bed.Processor().ProcessAsync(Item, TestContext.Current.CancellationToken);

        Assert.Equal(
            [$"removeTag {TenantId} {ConversationId} {CrmTagId}", $"moveDeal {TenantId} {ConversationId} 88888888-8888-8888-8888-888888888888"],
            crm.Calls);
        Assert.Equal(CrmResultCodes.Moved, Assert.Single(bed.Runs.Added).ToState().Variables.TextOf("crm2.result"));
    }

    /// <summary>
    /// Review Focus 4: the unit that applied the tag died before saving the run, which stayed <c>Running</c> at the block. The retried
    /// unit re-requests the effect (<see cref="Application.Engine.RunEffects.PendingInputAsync"/>); the port answers
    /// <c>already_*</c> or <c>not_tagged</c> and the walk goes on by <c>ok</c>. The change committed in Customers or Pipeline (with
    /// its <c>chatbot</c> audit row), so the retry writes the run's one «applied» row: it cannot tell who applied it, and one row per
    /// run and block is better than none.
    /// </summary>
    [Theory]
    [InlineData(CrmResultCodes.AlreadyTagged)]
    [InlineData(CrmResultCodes.NotTagged)]
    [InlineData(CrmResultCodes.AlreadyInStage)]
    public async Task ARunDiedWhileRunningIsReRequestedAndTheNoOpAnswerIsAuditedAsApplied(string code)
    {
        var crm = new RecordingCrm(CrmResultCodes.Added, code)
        {
            // The first call writes, then the unit dies (the worker is shut down) before the run is saved.
            FailAfterCall = 1,
        };
        var bed = new Bed(null, ActiveState(), flow: CrmFlow, crm: crm);
        var run = ConversationRun.Start(
            Guid.CreateVersion7(), TenantId, ConversationId, ConnectionId, ChatbotId, VersionId,
            RunState.Initial(RunVariables.Empty()) with { Status = RunStatus.Running, CurrentNodeId = "crm" }, DateTimeOffset.UtcNow.AddMinutes(-1));
        bed.Runs.Added.Add(run);

        var first = await bed.Processor().ProcessAsync(Item, TestContext.Current.CancellationToken);

        Assert.Equal(RunOutcomes.Retry, first);
        Assert.Equal((RunStatus.Running, "crm"), (run.Status, run.CurrentNodeId));
        Assert.Empty(bed.Messenger.Texts);

        // The in-memory run kept the first unit's inbound mark, which the database would have rolled back: the next unit is another row.
        await bed.Processor().ProcessAsync(Item with { MessageId = Guid.CreateVersion7() }, TestContext.Current.CancellationToken);

        Assert.Equal(2, crm.Calls.Count);
        Assert.Equal([$"ok {code}"], bed.Messenger.Texts);
        Assert.Single(bed.Audit.Actions, action => action == AutomationsAuditActions.CrmActionApplied);
        Assert.Equal(RunStatus.Completed, run.Status);
    }

    /// <summary>A concurrency conflict, a transient database failure or a cancellation is «not yet»: it goes up, the unit is undone and
    /// the row is retried, without leaving the block.</summary>
    [Fact]
    public async Task ATransientPortExceptionPropagatesWithoutAdvancing()
    {
        var crm = new RecordingCrm(CrmResultCodes.Added)
        {
            Failure = new RequestConcurrencyException("concurrency.conflict", "The customer changed."),
        };
        var bed = new Bed(null, ActiveState(), flow: CrmFlow, crm: crm);
        var run = ConversationRun.Start(
            Guid.CreateVersion7(), TenantId, ConversationId, ConnectionId, ChatbotId, VersionId,
            RunState.Initial(RunVariables.Empty()) with { Status = RunStatus.Running, CurrentNodeId = "crm" }, DateTimeOffset.UtcNow.AddMinutes(-1));
        bed.Runs.Added.Add(run);

        var outcome = await bed.Processor().ProcessAsync(Item, TestContext.Current.CancellationToken);

        Assert.Equal(RunOutcomes.Retry, outcome);
        Assert.Equal([$"retry {Item.MessageId} 1 00:00:02"], bed.Inbox.Calls);
        Assert.Equal((RunStatus.Running, "crm"), (run.Status, run.CurrentNodeId));
        Assert.Empty(bed.Messenger.Sent);
        Assert.Empty(bed.Conversations.Handoffs);
    }

    /// <summary>P2-R33a: anything else the port throws is the block's <c>error</c> exit with <c>failed</c>; the unit does not fail.</summary>
    [Fact]
    public async Task AnyOtherExceptionBecomesFailedOutputError()
    {
        var crm = new RecordingCrm(CrmResultCodes.Added) { Failure = new InvalidOperationException("Something broke.") };
        var bed = new Bed(null, flow: CrmFlow, crm: crm);

        await bed.Processor().ProcessAsync(Item, TestContext.Current.CancellationToken);

        var run = Assert.Single(bed.Runs.Added);
        Assert.Equal(["error failed"], bed.Messenger.Texts);
        Assert.Equal(CrmResultCodes.Failed, run.ToState().Variables.TextOf("crm.result"));
        Assert.Contains(AutomationsAuditActions.CrmActionFailed, bed.Audit.Actions);
        Assert.Equal(RunStatus.Completed, run.Status);
    }

    /// <summary>P2-R40: a block whose output comes back to it would loop inside the unit; it counts for the re-entry cap.</summary>
    [Fact]
    public async Task ACrmBlockCountsForTheReentryLimit()
    {
        const string flow = """
            { "schema": 1, "start": { "firstNodeId": "crm" }, "variables": [],
              "nodes": [
                { "id": "crm", "kind": "crmAction", "title": "Etiquetar", "operation": "addTag", "tagId": "77777777-7777-7777-7777-777777777777",
                  "stageId": null, "saveTo": "crm", "outputs": { "ok": "crm", "skipped": null, "error": null } }
              ] }
            """;
        var crm = new RecordingCrm(CrmResultCodes.AlreadyTagged);
        var bed = new Bed(null, flow: flow, crm: crm);

        await bed.Processor().ProcessAsync(Item, TestContext.Current.CancellationToken);

        var run = Assert.Single(bed.Runs.Added);
        Assert.Equal((RunStatus.Failed, "automations.run.loop_detected"), (run.Status, run.FailureCode));
        Assert.Equal(EngineReentries.Max, crm.Calls.Count);
        Assert.Equal(
            new HandoffCause(HandoffCauseKind.FlowFailed, "automations.run.loop_detected", "crm", "Etiquetar", null, null, null),
            Assert.Single(bed.Conversations.Causes));
    }

    /// <summary>A fake CRM port: answers <paramref name="answers"/> in order (the last one repeats) and records each call.</summary>
    private sealed class RecordingCrm(params string[] answers) : IAutomationCrm
    {
        public List<string> Calls { get; } = [];

        /// <summary>Thrown by every call instead of answering.</summary>
        public Exception? Failure { get; init; }

        /// <summary>The call number after which the unit dies (a concurrency conflict thrown after the write).</summary>
        public int? FailAfterCall { get; init; }

        public Task<string> AddTagAsync(CrmActionContext context, Guid tagId, CancellationToken cancellationToken) =>
            AnswerAsync($"addTag {context.TenantId} {context.ConversationId} {tagId}");

        public Task<string> RemoveTagAsync(CrmActionContext context, Guid tagId, CancellationToken cancellationToken) =>
            AnswerAsync($"removeTag {context.TenantId} {context.ConversationId} {tagId}");

        public Task<string> MoveDealAsync(CrmActionContext context, Guid stageId, CancellationToken cancellationToken) =>
            AnswerAsync($"moveDeal {context.TenantId} {context.ConversationId} {stageId}");

        private Task<string> AnswerAsync(string call)
        {
            Calls.Add(call);
            if (answers.Length == 0)
            {
                throw new NotSupportedException("The test expected no CRM block.");
            }

            if (Failure is not null)
            {
                return Task.FromException<string>(Failure);
            }

            if (FailAfterCall == Calls.Count)
            {
                return Task.FromException<string>(new RequestConcurrencyException("concurrency.conflict", "The unit died after the write."));
            }

            return Task.FromResult(answers[Math.Min(Calls.Count - 1, answers.Length - 1)]);
        }
    }
}
