using Modules.Wms.Domain;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Tasks;
using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.UnitTests.Tasks;

public sealed class WarehouseTaskTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Member = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Other = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly OwnerId Owner = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    private static readonly WarehouseId Warehouse = new(Guid.Parse("66666666-6666-6666-6666-666666666666"));
    private static readonly Guid Variant = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static WarehouseTaskLine Line(decimal qty = 5) =>
        WarehouseTaskLine.Create(Variant, null, StockStatus.Available, LocationId.New(), LocationId.New(), qty);

    private static WarehouseTask NewMove(
        bool publish = true,
        Guid? assignTo = null,
        WarehouseTaskLine[]? lines = null,
        int? priority = null,
        DateTimeOffset? dueAt = null,
        string? note = null) =>
        WarehouseTask.CreateMove(
            Tenant, "TSK-20261126-0001", Warehouse, Owner, priority, dueAt, note, lines ?? [Line()], assignTo, publish, Member, Now);

    [Fact]
    public void TheMovePolicyIsPriorityThreeFifteenMinutesWithOverrideAndInternalMove()
    {
        Assert.Equal(3, TaskTypePolicy.DefaultPriority(WarehouseTaskType.Move));
        Assert.Equal(TimeSpan.FromMinutes(15), TaskTypePolicy.LeaseDuration(WarehouseTaskType.Move));
        Assert.True(TaskTypePolicy.AllowsDestinationOverride(WarehouseTaskType.Move));
        Assert.Equal(MovementReason.InternalMove, TaskTypePolicy.MovementReasonOf(WarehouseTaskType.Move));
    }

    [Fact]
    public void CreatingAPublishedMoveIsReadyWithReleasedAtAndACreatedEvent()
    {
        var task = NewMove();

        Assert.Equal(WarehouseTaskStatus.Ready, task.Status);
        Assert.Equal(Now, task.ReleasedAt);
        Assert.Equal(3, task.Priority);
        Assert.Equal(1, task.Version);
        Assert.Equal([TaskEventKind.Created, TaskEventKind.Published], task.Events.Select(e => e.Kind));
        Assert.All(task.Events, e =>
        {
            Assert.Equal(Tenant, e.TenantId);
            Assert.Equal(task.Id, e.TaskId);
            Assert.Equal(Member, e.MemberId);
            Assert.Equal(Now, e.OccurredAt);
        });
    }

    [Fact]
    public void CreationKeepsTheDataOfTheTaskAndItsLines()
    {
        var first = Line(2);
        var second = Line(3);
        var dueAt = new DateTimeOffset(2026, 11, 26, 10, 0, 0, TimeSpan.FromHours(-5));

        var task = NewMove(lines: [first, second], priority: 7, dueAt: dueAt, note: "  reabastecer  ");

        Assert.Equal(Tenant, task.TenantId);
        Assert.Equal(Warehouse, task.WarehouseId);
        Assert.Equal(Owner, task.OwnerId);
        Assert.Equal("TSK-20261126-0001", task.Number);
        Assert.Equal(WarehouseTaskType.Move, task.Type);
        Assert.Equal(TaskSourceType.Manual, task.SourceType);
        Assert.Null(task.SourceId);
        Assert.Equal(7, task.Priority);
        Assert.False(task.Urgent);
        Assert.Equal(dueAt, task.DueAt);
        Assert.Equal(TimeSpan.Zero, task.DueAt!.Value.Offset);
        Assert.Equal("reabastecer", task.Note);
        Assert.Equal(Member, task.CreatedBy);
        Assert.Equal(Now, task.CreatedAt);
        Assert.Equal([first.Id, second.Id], task.Lines.Select(line => line.Id));
        Assert.Equal([0, 1], task.Lines.Select(line => line.Position));
        Assert.All(task.Lines, line =>
        {
            Assert.Equal(TaskLineStatus.Open, line.Status);
            Assert.Equal(0, line.QuantityConfirmed);
            Assert.Equal(line.QuantityPlanned, line.Remaining);
        });
    }

    [Fact]
    public void CreatingWithAnAssigneeAndPublishIsAssigned()
    {
        var task = NewMove(assignTo: Other);

        Assert.Equal(WarehouseTaskStatus.Assigned, task.Status);
        Assert.Equal(Other, task.AssignedMemberId);
        Assert.Equal(1, task.Version);
    }

    [Fact]
    public void CreatingUnpublishedIsPlannedWithoutReleasedAt()
    {
        var task = NewMove(publish: false);

        Assert.Equal(WarehouseTaskStatus.Planned, task.Status);
        Assert.Null(task.ReleasedAt);
        Assert.Equal([TaskEventKind.Created], task.Events.Select(e => e.Kind));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void AMoveHasBetweenOneAndOneHundredLines(int count)
    {
        var error = Assert.Throws<WmsDomainException>(() => NewMove(lines: Enumerable.Range(0, count).Select(_ => Line()).ToArray()));

        Assert.Equal(WmsErrorCodes.TaskLineInvalid, error.Code);
    }

    [Fact]
    public void AMoveAcceptsOneHundredLines() =>
        Assert.Equal(100, NewMove(lines: Enumerable.Range(0, 100).Select(_ => Line()).ToArray()).Lines.Count);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ALineQuantityMustBePositive(int quantity)
    {
        var error = Assert.Throws<WmsDomainException>(() => Line(quantity));

        Assert.Equal(WmsErrorCodes.TaskLineInvalid, error.Code);
    }

    [Fact]
    public void ALineCannotGoFromALocationToItself()
    {
        var location = LocationId.New();

        var error = Assert.Throws<WmsDomainException>(
            () => WarehouseTaskLine.Create(Variant, null, StockStatus.Available, location, location, 1));

        Assert.Equal(WmsErrorCodes.TaskLocationInvalid, error.Code);
    }

    [Fact]
    public void AMoveLineNeedsADestination()
    {
        var line = WarehouseTaskLine.Create(Variant, null, StockStatus.Available, LocationId.New(), null, 1);

        var error = Assert.Throws<WmsDomainException>(() => NewMove(lines: [line]));

        Assert.Equal(WmsErrorCodes.TaskLocationInvalid, error.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    public void PriorityGoesFromOneToNine(int priority)
    {
        var created = Assert.Throws<WmsDomainException>(() => NewMove(priority: priority));
        var changed = Assert.Throws<WmsDomainException>(() => NewMove().ChangePriority(priority, false, Member, Now));

        Assert.Equal(WmsErrorCodes.TaskLineInvalid, created.Code);
        Assert.Equal(WmsErrorCodes.TaskLineInvalid, changed.Code);
    }

    [Fact]
    public void PublishingAReadyTaskIsAnInvalidTransition()
    {
        var task = NewMove();

        var error = Assert.Throws<TaskTransitionException>(() => task.Publish(Member, Now));

        Assert.Equal(WmsErrorCodes.TaskInvalidTransition, error.Code);
        Assert.Equal("Ready", error.ProblemExtensions["from"]);
        Assert.Equal("publish", error.ProblemExtensions["action"]);
        Assert.Equal(1, task.Version);
        Assert.Equal(2, task.Events.Count);
    }

    [Fact]
    public void PublishingAPlannedTaskReleasesIt()
    {
        var task = NewMove(publish: false);
        var later = Now.AddHours(1);

        task.Publish(Other, later);

        Assert.Equal(WarehouseTaskStatus.Ready, task.Status);
        Assert.Equal(later, task.ReleasedAt);
        Assert.Equal(2, task.Version);
        Assert.Equal(TaskEventKind.Published, task.Events[^1].Kind);
        Assert.Equal(Other, task.Events[^1].MemberId);
    }

    [Fact]
    public void HoldingAnAssignedTaskKeepsTheAssigneeAndPublishingReturnsItToAssigned()
    {
        var task = NewMove(assignTo: Other);

        task.Hold(Member, "  falta espacio  ", Now);

        Assert.Equal(WarehouseTaskStatus.Planned, task.Status);
        Assert.Equal(Other, task.AssignedMemberId);
        Assert.Null(task.ReleasedAt);
        Assert.Equal(2, task.Version);
        Assert.Equal(TaskEventKind.Held, task.Events[^1].Kind);
        Assert.Equal("falta espacio", task.Events[^1].Detail);

        task.Publish(Member, Now.AddMinutes(5));

        Assert.Equal(WarehouseTaskStatus.Assigned, task.Status);
        Assert.Equal(Now.AddMinutes(5), task.ReleasedAt);
        Assert.Equal(3, task.Version);
    }

    [Fact]
    public void HoldingAPlannedTaskIsAnInvalidTransition()
    {
        var error = Assert.Throws<TaskTransitionException>(() => NewMove(publish: false).Hold(Member, null, Now));

        Assert.Equal("Planned", error.ProblemExtensions["from"]);
        Assert.Equal("hold", error.ProblemExtensions["action"]);
    }

    [Fact]
    public void AssigningAReadyTaskMakesItAssignedAndRaisesTheVersion()
    {
        var task = NewMove();

        task.Assign(Other, Member, null, Now);

        Assert.Equal(WarehouseTaskStatus.Assigned, task.Status);
        Assert.Equal(Other, task.AssignedMemberId);
        Assert.Equal(2, task.Version);
        Assert.Equal(TaskEventKind.Assigned, task.Events[^1].Kind);
        Assert.Equal(Member, task.Events[^1].MemberId);
    }

    [Fact]
    public void AssigningAnAssignedTaskChangesTheAssignee()
    {
        var task = NewMove(assignTo: Other);

        task.Assign(Member, Member, "turno", Now);

        Assert.Equal(WarehouseTaskStatus.Assigned, task.Status);
        Assert.Equal(Member, task.AssignedMemberId);
        Assert.Equal("turno", task.Events[^1].Detail);
    }

    [Fact]
    public void AssigningAPlannedTaskKeepsItPlanned()
    {
        var task = NewMove(publish: false);

        task.Assign(Other, Member, null, Now);

        Assert.Equal(WarehouseTaskStatus.Planned, task.Status);
        Assert.Equal(Other, task.AssignedMemberId);
        Assert.Equal(2, task.Version);
    }

    [Fact]
    public void UnassigningAnAssignedTaskReturnsItToThePool()
    {
        var task = NewMove(assignTo: Other);

        task.Unassign(Member, Now);

        Assert.Equal(WarehouseTaskStatus.Ready, task.Status);
        Assert.Null(task.AssignedMemberId);
        Assert.Equal(2, task.Version);
        Assert.Equal(TaskEventKind.Unassigned, task.Events[^1].Kind);
    }

    [Fact]
    public void UnassigningAPlannedTaskKeepsItPlannedWithoutAssignee()
    {
        var task = NewMove(publish: false, assignTo: Other);

        task.Unassign(Member, Now);

        Assert.Equal(WarehouseTaskStatus.Planned, task.Status);
        Assert.Null(task.AssignedMemberId);
    }

    [Theory]
    [InlineData(true, "Ready")]
    [InlineData(false, "Planned")]
    public void UnassigningATaskWithoutAssigneeIsAnInvalidTransition(bool publish, string from)
    {
        var error = Assert.Throws<TaskTransitionException>(() => NewMove(publish).Unassign(Member, Now));

        Assert.Equal(from, error.ProblemExtensions["from"]);
        Assert.Equal("unassign", error.ProblemExtensions["action"]);
    }

    [Fact]
    public void ChangingPriorityStoresPriorityAndUrgency()
    {
        var task = NewMove();

        task.ChangePriority(9, true, Other, Now);

        Assert.Equal(9, task.Priority);
        Assert.True(task.Urgent);
        Assert.Equal(WarehouseTaskStatus.Ready, task.Status);
        Assert.Equal(2, task.Version);
        Assert.Equal(TaskEventKind.PriorityChanged, task.Events[^1].Kind);
    }

    [Fact]
    public void CancellingClosesTheOpenLinesAndIsTerminal()
    {
        var task = NewMove(lines: [Line(), Line()]);

        task.Cancel("  pedido anulado  ", Other, Now.AddMinutes(1));

        Assert.Equal(WarehouseTaskStatus.Cancelled, task.Status);
        Assert.Equal(Now.AddMinutes(1), task.CancelledAt);
        Assert.Equal("pedido anulado", task.CancelReason);
        Assert.All(task.Lines, line =>
        {
            Assert.Equal(TaskLineStatus.Cancelled, line.Status);
            Assert.Equal("pedido anulado", line.CancelReason);
        });
        Assert.Equal(2, task.Version);
        Assert.Equal(TaskEventKind.Cancelled, task.Events[^1].Kind);
        Assert.Equal("pedido anulado", task.Events[^1].Detail);

        var again = Assert.Throws<TaskTransitionException>(() => task.Cancel("otra vez", Other, Now));
        Assert.Equal("Cancelled", again.ProblemExtensions["from"]);
        Assert.Equal("cancel", again.ProblemExtensions["action"]);
    }

    [Fact]
    public void ACancelledTaskAdmitsNoSupervisorAction()
    {
        var task = NewMove();
        var line = task.Lines[0];
        task.Cancel("duplicada", Member, Now);

        var actions = new (string Action, Action Run)[]
        {
            ("publish", () => task.Publish(Member, Now)),
            ("hold", () => task.Hold(Member, null, Now)),
            ("assign", () => task.Assign(Other, Member, null, Now)),
            ("unassign", () => task.Unassign(Member, Now)),
            ("change-priority", () => task.ChangePriority(5, false, Member, Now)),
            ("cancel-line", () => task.CancelLine(line.Id, "duplicada", Member, Now)),
        };

        foreach (var (action, run) in actions)
        {
            var error = Assert.Throws<TaskTransitionException>(run);
            Assert.Equal("Cancelled", error.ProblemExtensions["from"]);
            Assert.Equal(action, error.ProblemExtensions["action"]);
        }

        Assert.Equal(2, task.Version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void CancellingRequiresAReason(string? reason)
    {
        var task = NewMove();

        var whole = Assert.Throws<WmsDomainException>(() => task.Cancel(reason!, Member, Now));
        var line = Assert.Throws<WmsDomainException>(() => task.CancelLine(task.Lines[0].Id, reason!, Member, Now));

        Assert.Equal(WmsErrorCodes.TaskLineInvalid, whole.Code);
        Assert.Equal(WmsErrorCodes.TaskLineInvalid, line.Code);
        Assert.Equal(WarehouseTaskStatus.Ready, task.Status);
        Assert.Equal(1, task.Version);
    }

    [Fact]
    public void AReasonHasAtMostFiveHundredCharacters()
    {
        var task = NewMove();
        var tooLong = new string('a', 501);

        var errors = new[]
        {
            Assert.Throws<WmsDomainException>(() => task.Cancel(tooLong, Member, Now)),
            Assert.Throws<WmsDomainException>(() => task.CancelLine(task.Lines[0].Id, tooLong, Member, Now)),
            Assert.Throws<WmsDomainException>(() => task.Hold(Member, tooLong, Now)),
            Assert.Throws<WmsDomainException>(() => task.Assign(Other, Member, tooLong, Now)),
        };

        Assert.All(errors, error => Assert.Equal(WmsErrorCodes.TaskLineInvalid, error.Code));
        Assert.Equal(1, task.Version);
    }

    [Fact]
    public void CancellingOneOfTwoLinesKeepsTheTaskOpen()
    {
        var task = NewMove(lines: [Line(), Line()]);
        var cancelled = task.Lines[0];

        task.CancelLine(cancelled.Id, "sin stock", Other, Now);

        Assert.Equal(WarehouseTaskStatus.Ready, task.Status);
        Assert.Equal(TaskLineStatus.Cancelled, cancelled.Status);
        Assert.Equal("sin stock", cancelled.CancelReason);
        Assert.Equal(TaskLineStatus.Open, task.Lines[1].Status);
        Assert.Equal(2, task.Version);
        Assert.Equal(TaskEventKind.LineCancelled, task.Events[^1].Kind);
        Assert.Equal(cancelled.Id, task.Events[^1].LineId);
        Assert.Equal("sin stock", task.Events[^1].Detail);
    }

    [Fact]
    public void CancellingTheOnlyOpenLineCancelsTheTask()
    {
        var task = NewMove();

        task.CancelLine(task.Lines[0].Id, "sin stock", Other, Now);

        Assert.Equal(WarehouseTaskStatus.Cancelled, task.Status);
        Assert.Equal(Now, task.CancelledAt);
        Assert.Equal("sin stock", task.CancelReason);
        Assert.Null(task.CompletedAt);
        Assert.Equal(2, task.Version);
        Assert.Equal([TaskEventKind.LineCancelled, TaskEventKind.Cancelled], task.Events.Skip(2).Select(e => e.Kind));
    }

    [Fact]
    public void CancellingACancelledLineIsAnInvalidTransition()
    {
        var task = NewMove(lines: [Line(), Line()]);
        var line = task.Lines[0];
        task.CancelLine(line.Id, "sin stock", Member, Now);

        var error = Assert.Throws<TaskTransitionException>(() => task.CancelLine(line.Id, "sin stock", Member, Now));

        Assert.Equal("Cancelled", error.ProblemExtensions["from"]);
        Assert.Equal("cancel-line", error.ProblemExtensions["action"]);
        Assert.Equal(2, task.Version);
    }

    [Fact]
    public void ALineIsFoundOnlyInItsOwnTask()
    {
        var task = NewMove();

        Assert.Same(task.Lines[0], task.FindLine(task.Lines[0].Id));
        Assert.Null(task.FindLine(Guid.NewGuid()));
    }
}
