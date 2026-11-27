using Modules.Wms.Domain;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Tasks;
using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.UnitTests.Tasks;

public sealed class WarehouseTaskShortTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Member = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Supervisor = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Expired = Now.AddMinutes(16);
    private const string Device = "zebra-01";

    private static WarehouseTaskLine Line(decimal qty = 5) =>
        WarehouseTaskLine.Create(Guid.NewGuid(), null, StockStatus.Available, LocationId.New(), LocationId.New(), qty);

    private static (WarehouseTask Task, Guid Token) Claimed(params WarehouseTaskLine[] lines)
    {
        var task = WarehouseTask.CreateMove(
            Tenant, "TSK-20261126-0001", WarehouseId.New(), OwnerId.New(), null, null, null,
            lines.Length == 0 ? [Line()] : lines, null, true, Supervisor, Now);
        task.Claim(Member, Device, Now);
        return (task, task.LeaseToken!.Value);
    }

    [Fact]
    public void ShortingWithTwoFoundConfirmsTwoAndClosesTheLineAsShort()
    {
        var (task, token) = Claimed();
        var lineId = task.Lines[0].Id;

        var line = task.ShortLine(Member, token, Device, lineId, 2, Now.AddMinutes(1));

        Assert.Equal(TaskLineStatus.Short, line.Status);
        Assert.Equal(2, line.QuantityConfirmed);
        var confirmed = Assert.Single(task.Events, e => e.Kind == TaskEventKind.LineConfirmed);
        Assert.Equal(2, confirmed.Quantity);
        var missing = Assert.Single(task.Events, e => e.Kind == TaskEventKind.LineShort);
        Assert.Equal(3, missing.Quantity);
        Assert.Equal(lineId, missing.LineId);
        Assert.Equal(WarehouseTaskStatus.Done, task.Status);
        Assert.Equal(Now.AddMinutes(1), task.CompletedAt);
        Assert.Contains(task.Events, e => e.Kind == TaskEventKind.Completed);
    }

    [Fact]
    public void ShortingWithNothingFoundClosesTheTaskAsDone()
    {
        var (task, token) = Claimed();

        task.ShortLine(Member, token, Device, task.Lines[0].Id, 0, Now.AddMinutes(1));

        Assert.DoesNotContain(task.Events, e => e.Kind == TaskEventKind.LineConfirmed);
        Assert.Equal(5, Assert.Single(task.Events, e => e.Kind == TaskEventKind.LineShort).Quantity);
        Assert.Equal(TaskLineStatus.Short, task.Lines[0].Status);
        Assert.Equal(WarehouseTaskStatus.Done, task.Status);
    }

    [Fact]
    public void AShortLineWithAnotherCancelledStillClosesDone()
    {
        var (task, token) = Claimed(Line(5), Line(3));
        task.CancelLine(task.Lines[0].Id, "sin stock", Supervisor, Now.AddMinutes(1));

        task.ShortLine(Member, token, Device, task.Lines[1].Id, 1, Now.AddMinutes(2));

        Assert.Equal(WarehouseTaskStatus.Done, task.Status);
    }

    [Fact]
    public void ShortingAfterAPartialConfirmationOnlyCountsTheRest()
    {
        var (task, token) = Claimed();
        var lineId = task.Lines[0].Id;
        task.ConfirmLine(Member, token, Device, lineId, 1, Now.AddMinutes(1));

        task.ShortLine(Member, token, Device, lineId, 1, Now.AddMinutes(2));

        Assert.Equal(2, task.Lines[0].QuantityConfirmed);
        Assert.Equal(3, Assert.Single(task.Events, e => e.Kind == TaskEventKind.LineShort).Quantity);
    }

    [Fact]
    public void FindingEverythingIsNotAShort()
    {
        var (task, token) = Claimed();

        var error = Assert.Throws<WmsDomainException>(
            () => task.ShortLine(Member, token, Device, task.Lines[0].Id, 5, Now.AddMinutes(1)));

        Assert.Equal(WmsErrorCodes.TaskLineInvalid, error.Code);
        Assert.Equal(TaskLineStatus.Open, task.Lines[0].Status);
    }

    [Fact]
    public void FindingMoreThanRemainingIs422()
    {
        var (task, token) = Claimed();

        var error = Assert.Throws<WmsDomainException>(
            () => task.ShortLine(Member, token, Device, task.Lines[0].Id, 6, Now.AddMinutes(1)));

        Assert.Equal(WmsErrorCodes.TaskQuantityExceedsRemaining, error.Code);
    }

    [Fact]
    public void ANegativeFoundIs422LineInvalid()
    {
        var (task, token) = Claimed();

        var error = Assert.Throws<WmsDomainException>(
            () => task.ShortLine(Member, token, Device, task.Lines[0].Id, -1, Now.AddMinutes(1)));

        Assert.Equal(WmsErrorCodes.TaskLineInvalid, error.Code);
    }

    [Fact]
    public void ShortingAClosedLineIsAnInvalidTransition()
    {
        var (task, token) = Claimed(Line(5), Line(3));
        var lineId = task.Lines[0].Id;
        task.ShortLine(Member, token, Device, lineId, 0, Now.AddMinutes(1));

        var error = Assert.Throws<TaskTransitionException>(
            () => task.ShortLine(Member, token, Device, lineId, 0, Now.AddMinutes(2)));

        Assert.Equal("Short", error.ProblemExtensions["from"]);
        Assert.Equal("short-line", error.ProblemExtensions["action"]);
    }

    [Fact]
    public void ConfirmingAShortLineIsAnInvalidTransition()
    {
        var (task, token) = Claimed(Line(5), Line(3));
        var lineId = task.Lines[0].Id;
        task.ShortLine(Member, token, Device, lineId, 0, Now.AddMinutes(1));

        var error = Assert.Throws<TaskTransitionException>(
            () => task.ConfirmLine(Member, token, Device, lineId, 1, Now.AddMinutes(2)));

        Assert.Equal("Short", error.ProblemExtensions["from"]);
        Assert.Equal("confirm-line", error.ProblemExtensions["action"]);
    }

    [Fact]
    public void AResumableLeaseResumesBeforeShorting()
    {
        var (task, token) = Claimed();
        task.ExpireLease(Expired);

        task.ShortLine(Member, token, Device, task.Lines[0].Id, 2, Expired.AddMinutes(1));

        Assert.Equal(
            [
                TaskEventKind.LeaseExpired,
                TaskEventKind.LeaseResumed,
                TaskEventKind.LineConfirmed,
                TaskEventKind.LineShort,
                TaskEventKind.Completed,
            ],
            task.Events.TakeLast(5).Select(e => e.Kind));
    }

    [Fact]
    public void ABusyMemberShortsWithoutResuming()
    {
        var (task, token) = Claimed(Line(5), Line(3));
        task.ExpireLease(Expired);
        var status = task.Status;

        task.ShortLine(Member, token, Device, task.Lines[0].Id, 2, Expired.AddMinutes(1), memberIsBusy: true);

        Assert.Equal(status, task.Status);
        Assert.DoesNotContain(task.Events, e => e.Kind == TaskEventKind.LeaseResumed);
        Assert.Equal(TaskLineStatus.Short, task.Lines[0].Status);
        Assert.Null(task.LeaseExpiresAt);
    }

    [Fact]
    public void ALostLeaseThrowsInvalidOperation()
    {
        var (task, _) = Claimed();

        Assert.Throws<InvalidOperationException>(
            () => task.ShortLine(Member, Guid.NewGuid(), Device, task.Lines[0].Id, 1, Now));
    }

    [Fact]
    public void ShortEventsCarryTheScanTime()
    {
        var (task, token) = Claimed();
        var scanned = Now.AddMinutes(5).ToOffset(TimeSpan.FromHours(-5));
        var synced = Now.AddMinutes(20);

        task.ShortLine(Member, token, Device, task.Lines[0].Id, 2, synced, scanned);

        var events = task.Events.Where(e => e.Kind is TaskEventKind.LineConfirmed or TaskEventKind.LineShort or TaskEventKind.Completed)
            .ToList();
        Assert.Equal(3, events.Count);
        Assert.All(events, e =>
        {
            Assert.Equal(scanned, e.OccurredAt);
            Assert.Equal(TimeSpan.Zero, e.OccurredAt.Offset);
            Assert.Equal(synced, e.RecordedAt);
        });
    }

    [Theory]
    [InlineData(-43201)]
    [InlineData(6)]
    public void AScanOutsideTheLedgerWindowIs422(int minutes)
    {
        var (task, token) = Claimed();

        var error = Assert.Throws<WmsDomainException>(() => task.ShortLine(
            Member, token, Device, task.Lines[0].Id, 1, Now, Now.AddMinutes(minutes)));

        Assert.Equal(WmsErrorCodes.MovementOccurredAtOutOfRange, error.Code);
        Assert.Equal(TaskLineStatus.Open, task.Lines[0].Status);
    }

    [Fact]
    public void AScanMoreThanFiveMinutesBeforeTheReleaseIs422()
    {
        var (task, token) = Claimed();

        var error = Assert.Throws<WmsDomainException>(() => task.ShortLine(
            Member, token, Device, task.Lines[0].Id, 1, Expired, Now.AddMinutes(-6)));

        Assert.Equal(WmsErrorCodes.MovementOccurredAtOutOfRange, error.Code);
        Assert.Equal(0, task.Lines[0].QuantityConfirmed);
    }

    [Fact]
    public void AScanFourMinutesBeforeTheReleaseIsAccepted()
    {
        var (task, token) = Claimed();

        task.ShortLine(Member, token, Device, task.Lines[0].Id, 1, Expired, Now.AddMinutes(-4));

        Assert.Equal(TaskLineStatus.Short, task.Lines[0].Status);
    }
}
