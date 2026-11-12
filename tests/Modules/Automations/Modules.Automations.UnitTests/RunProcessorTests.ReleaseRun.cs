using BuildingBlocks.Application;
using Modules.Automations.Application;
using Modules.Automations.Application.Engine;
using Modules.Automations.Domain;
using Modules.Automations.Domain.Engine;

namespace Modules.Automations.UnitTests;

/// <summary>Spec 2026-11-03 D-4 y D-5: la señal «una persona tomó la conversación» cierra la corrida viva del bot sin arrancar ni avanzar
/// nada. Relee el estado de la conversación en vez de confiar en la señal, y sólo cierra una corrida que arrancó antes que ella.</summary>
public sealed partial class RunProcessorTests
{
    private static InboundQueueItem ReleaseItem(DateTimeOffset createdAt) =>
        new(Guid.CreateVersion7(), TenantId, ConnectionId, ConversationId, Opened: false, createdAt, Attempts: 1, ReleaseRun: true);

    private static AutomationConversationState PersonState(string state, DateTimeOffset runStartedAt)
    {
        var now = DateTimeOffset.UtcNow;
        var member = Guid.CreateVersion7();
        return state switch
        {
            "resolved" => new("Resolved", null, "None", null, ConnectionId, now, "Laura", "573001234567"),
            "took" => new("Open", member, "Paused", ChatbotId, ConnectionId, now, "Laura", "573001234567", AutomationPauseReason: "AdvisorTook"),
            "transferred" => new("Open", member, "Paused", ChatbotId, ConnectionId, now, "Laura", "573001234567", AutomationPauseReason: "AdvisorTransferred"),
            "replied" => new("Open", member, "Paused", ChatbotId, ConnectionId, now, "Laura", "573001234567", AutomationPauseReason: "AdvisorReplied"),
            _ => new("Open", null, "Active", ChatbotId, ConnectionId, now, "Laura", "573001234567", AutomationResumedAt: runStartedAt.AddSeconds(30)),
        };
    }

    [Theory]
    [InlineData("resolved")]
    [InlineData("took")]
    [InlineData("transferred")]
    [InlineData("replied")]
    [InlineData("resumed")]
    public async Task AReleaseRowClosesTheLiveRunWhenAPersonTookOver(string state)
    {
        var created = DateTimeOffset.UtcNow;
        var run = WaitingRun(created.AddMinutes(-1));
        var bed = new Bed(null, PersonState(state, run.StartedAt));
        bed.Runs.Added.Add(run);
        var item = ReleaseItem(created);
        bed.Inbox.NotInbound.Add(item.MessageId);

        var outcome = await bed.Processor().ProcessAsync(item, TestContext.Current.CancellationToken);

        Assert.Equal(RunOutcomes.Interrupted, outcome);
        Assert.Equal([$"complete {item.MessageId} 1 interrupted"], bed.Inbox.Calls);
        Assert.Equal((RunStatus.Interrupted, (Guid?)item.MessageId), (run.Status, run.LastInboundMessageId));
        Assert.Equal(1, bed.Unit.Saves);
        Assert.Empty(bed.Messenger.Sent);
        Assert.Empty(bed.Audit.Actions);
        Assert.Empty(bed.Conversations.Handoffs);
        Assert.Equal((0, 0), (bed.Conversations.Started, bed.Conversations.Ended));
        Assert.Single(bed.Runs.Added);
    }

    [Fact]
    public async Task AReleaseRowClosesTheRunOfADeletedConversation()
    {
        var created = DateTimeOffset.UtcNow;
        var bed = new Bed(null);
        bed.Conversations.Missing = true;
        bed.Runs.Added.Add(WaitingRun(created.AddMinutes(-1)));
        var item = ReleaseItem(created);
        bed.Inbox.NotInbound.Add(item.MessageId);

        var outcome = await bed.Processor().ProcessAsync(item, TestContext.Current.CancellationToken);

        Assert.Equal(RunOutcomes.Interrupted, outcome);
    }

    /// <summary>Review Focus 1: una corrida que arrancó en el mismo instante de la señal es posterior o simultánea a ella, y sigue viva.</summary>
    [Fact]
    public async Task AReleaseRowDoesNotCloseARunThatStartedAtItsInstant()
    {
        var created = DateTimeOffset.UtcNow;
        var run = WaitingRun(created);
        var bed = new Bed(null, PersonState("resolved", created));
        bed.Runs.Added.Add(run);
        var item = ReleaseItem(created);
        bed.Inbox.NotInbound.Add(item.MessageId);

        var outcome = await bed.Processor().ProcessAsync(item, TestContext.Current.CancellationToken);

        Assert.Equal(RunOutcomes.Skipped, outcome);
        Assert.Equal(RunStatus.Waiting, run.Status);
        Assert.Equal(0, bed.Unit.Saves);
        Assert.Equal([$"complete {item.MessageId} 1 skipped"], bed.Inbox.Calls);
    }

    [Fact]
    public async Task AReleaseRowDoesNotCloseARunThatStartedAfterIt()
    {
        var created = DateTimeOffset.UtcNow;
        var run = WaitingRun(created.AddSeconds(1));
        var bed = new Bed(null, PersonState("resolved", created));
        bed.Runs.Added.Add(run);
        var item = ReleaseItem(created);
        bed.Inbox.NotInbound.Add(item.MessageId);

        var outcome = await bed.Processor().ProcessAsync(item, TestContext.Current.CancellationToken);

        Assert.Equal(RunOutcomes.Skipped, outcome);
        Assert.Equal(RunStatus.Waiting, run.Status);
        Assert.Equal(0, bed.Unit.Saves);
        Assert.Equal([$"complete {item.MessageId} 1 skipped"], bed.Inbox.Calls);
    }

    [Fact]
    public async Task AReleaseRowDoesNotCloseWhenThePersonUndidIt()
    {
        var created = DateTimeOffset.UtcNow;
        var run = WaitingRun(created.AddMinutes(-1));
        var bed = new Bed(null, ActiveState());
        bed.Runs.Added.Add(run);
        var item = ReleaseItem(created);
        bed.Inbox.NotInbound.Add(item.MessageId);

        var outcome = await bed.Processor().ProcessAsync(item, TestContext.Current.CancellationToken);

        Assert.Equal(RunOutcomes.Skipped, outcome);
        Assert.Equal(RunStatus.Waiting, run.Status);
        Assert.Equal(0, bed.Unit.Saves);
    }

    [Fact]
    public async Task AReleaseRowNeverStartsARun()
    {
        var bed = new Bed(null, ActiveState(), trigger: ChatbotTrigger.NewConversation);
        var item = ReleaseItem(DateTimeOffset.UtcNow);
        bed.Inbox.NotInbound.Add(item.MessageId);

        var outcome = await bed.Processor().ProcessAsync(item, TestContext.Current.CancellationToken);

        Assert.Equal(RunOutcomes.Skipped, outcome);
        Assert.Empty(bed.Runs.Added);
        Assert.Empty(bed.Messenger.Sent);
        Assert.Equal(0, bed.Conversations.Started);
    }

    [Fact]
    public async Task AReleaseRowIgnoresTheDisabledModule()
    {
        var created = DateTimeOffset.UtcNow;
        var run = WaitingRun(created.AddMinutes(-1));
        var bed = new Bed(null, PersonState("resumed", run.StartedAt), moduleEnabled: false);
        bed.Runs.Added.Add(run);
        var item = ReleaseItem(created);
        bed.Inbox.NotInbound.Add(item.MessageId);

        var outcome = await bed.Processor().ProcessAsync(item, TestContext.Current.CancellationToken);

        Assert.Equal(RunOutcomes.Interrupted, outcome);
        Assert.Equal(0, bed.Conversations.Ended);
    }

    [Fact]
    public async Task AReleaseRowWithoutLiveRunIsSkipped()
    {
        var bed = new Bed(null, PersonState("resolved", DateTimeOffset.UtcNow));
        var item = ReleaseItem(DateTimeOffset.UtcNow);
        bed.Inbox.NotInbound.Add(item.MessageId);

        var outcome = await bed.Processor().ProcessAsync(item, TestContext.Current.CancellationToken);

        Assert.Equal(RunOutcomes.Skipped, outcome);
        Assert.Equal(0, bed.Unit.Saves);
    }

    [Fact]
    public async Task AReleaseRowAlreadyAppliedIsDuplicate()
    {
        var created = DateTimeOffset.UtcNow;
        var run = WaitingRun(created.AddMinutes(-1));
        var item = ReleaseItem(created);
        run.Finish(RunStatus.Interrupted, null, created);
        run.MarkInbound(item.MessageId);
        var bed = new Bed(null, PersonState("resolved", run.StartedAt));
        bed.Runs.Added.Add(run);
        bed.Inbox.NotInbound.Add(item.MessageId);

        var outcome = await bed.Processor().ProcessAsync(item, TestContext.Current.CancellationToken);

        Assert.Equal(RunOutcomes.Duplicate, outcome);
        Assert.Equal(0, bed.Conversations.Reads);
    }

    /// <summary>Una excepción no prevista se reintenta por el lease: la rama de la fila manual (traspasar y cerrar) no le toca a esta señal.</summary>
    [Fact]
    public async Task AReleaseRowThatThrowsIsRetriedNotHandedOff()
    {
        var created = DateTimeOffset.UtcNow;
        var bed = new Bed(null);
        bed.Conversations.ReadFailure = new InvalidOperationException("boom");
        bed.Runs.Added.Add(WaitingRun(created.AddMinutes(-1)));
        var item = ReleaseItem(created);
        bed.Inbox.NotInbound.Add(item.MessageId);

        await Assert.ThrowsAsync<InvalidOperationException>(() => bed.Processor().ProcessAsync(item, TestContext.Current.CancellationToken));

        Assert.Empty(bed.Conversations.Handoffs);
        Assert.Empty(bed.Inbox.Calls);
    }

    [Fact]
    public async Task ALockedConversationReschedulesTheReleaseRow()
    {
        var created = DateTimeOffset.UtcNow;
        var run = WaitingRun(created.AddMinutes(-1));
        var bed = new Bed(null, PersonState("resolved", run.StartedAt), lockBusy: true);
        bed.Runs.Added.Add(run);
        var item = ReleaseItem(created);
        bed.Inbox.NotInbound.Add(item.MessageId);

        var outcome = await bed.Processor().ProcessAsync(item, TestContext.Current.CancellationToken);

        Assert.Equal(RunOutcomes.Locked, outcome);
        Assert.Equal([$"retry {item.MessageId} 1 00:00:05"], bed.Inbox.Calls);
        Assert.Equal(RunStatus.Waiting, run.Status);
    }

    /// <summary>Review Focus 5: una señal que no cierra nada deja intacto el camino perezoso (D-M15) del siguiente entrante.</summary>
    [Fact]
    public async Task ASkippedReleaseRowLeavesTheLazyPathIntact()
    {
        var created = DateTimeOffset.UtcNow;
        var run = WaitingRun(created.AddSeconds(1));
        var bed = new Bed(null, PersonState("took", run.StartedAt));
        bed.Runs.Added.Add(run);
        var item = ReleaseItem(created);
        bed.Inbox.NotInbound.Add(item.MessageId);

        var first = await bed.Processor().ProcessAsync(item, TestContext.Current.CancellationToken);
        var second = await bed.Processor().ProcessAsync(
            Item with { MessageId = Guid.CreateVersion7(), Opened = false }, TestContext.Current.CancellationToken);

        Assert.Equal(RunOutcomes.Skipped, first);
        Assert.Equal(RunOutcomes.Interrupted, second);
        Assert.Equal(RunStatus.Interrupted, run.Status);
    }
}
