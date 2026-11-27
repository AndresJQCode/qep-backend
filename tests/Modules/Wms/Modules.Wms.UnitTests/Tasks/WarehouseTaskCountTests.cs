using Modules.Wms.Domain;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Tasks;
using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.UnitTests.Tasks;

public sealed class WarehouseTaskCountTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Member = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Variant = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid SourceIncident = Guid.Parse("88888888-8888-8888-8888-888888888888");
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly OwnerId Owner = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    private static readonly WarehouseId Warehouse = new(Guid.Parse("66666666-6666-6666-6666-666666666666"));
    private const string Device = "zebra-01";

    private static WarehouseTaskLine CountLine(LotId? lot = null) =>
        WarehouseTaskLine.CreateCount(Variant, lot, StockStatus.Available, LocationId.New());

    private static WarehouseTask NewCount(params WarehouseTaskLine[] lines) =>
        WarehouseTask.CreateCount(
            Tenant, "TSK-20261126-0001", Warehouse, Owner, lines.Length == 0 ? [CountLine()] : lines, TaskSourceType.Incident,
            SourceIncident, Now);

    private static (WarehouseTask Task, Guid Token) Claimed(params WarehouseTaskLine[] lines)
    {
        var task = NewCount(lines);
        task.Claim(Member, Device, Now);
        return (task, task.LeaseToken!.Value);
    }

    [Fact]
    public void ACountIsBornReadyFromTheSystemWithItsSource()
    {
        var task = NewCount();

        Assert.Equal(WarehouseTaskType.Count, task.Type);
        Assert.Equal(WarehouseTaskStatus.Ready, task.Status);
        Assert.Equal(Now, task.ReleasedAt);
        Assert.Equal(3, task.Priority);
        Assert.Equal(TaskSourceType.Incident, task.SourceType);
        Assert.Equal(SourceIncident, task.SourceId);
        Assert.Equal(Guid.Empty, task.CreatedBy);
        Assert.Equal(1, task.Version);
        Assert.Equal([TaskEventKind.Created, TaskEventKind.Published], task.Events.Select(e => e.Kind));
        Assert.All(task.Events, e => Assert.Null(e.MemberId));
    }

    [Fact]
    public void ACountLineHasNoDestinationAndNothingPlanned()
    {
        var line = CountLine();

        Assert.Null(line.ToLocationId);
        Assert.Equal(0, line.QuantityPlanned);
        Assert.Equal(0, line.Remaining);
        Assert.Null(line.QuantityCounted);
        Assert.Equal(TaskLineStatus.Open, line.Status);
    }

    [Fact]
    public void ACountNeedsBetweenOneAndOneHundredLines()
    {
        var none = Assert.Throws<WmsDomainException>(
            () => WarehouseTask.CreateCount(
                Tenant, "TSK-20261126-0001", Warehouse, Owner, [], TaskSourceType.Incident, SourceIncident, Now));
        var many = Assert.Throws<WmsDomainException>(
            () => WarehouseTask.CreateCount(
                Tenant, "TSK-20261126-0001", Warehouse, Owner, [.. Enumerable.Range(0, 101).Select(_ => CountLine())],
                TaskSourceType.Incident, SourceIncident, Now));

        Assert.Equal(WmsErrorCodes.TaskLineInvalid, none.Code);
        Assert.Equal(WmsErrorCodes.TaskLineInvalid, many.Code);
    }

    [Fact]
    public void ACountOnlyAcceptsCountLines()
    {
        var moveLine = WarehouseTaskLine.Create(Variant, null, StockStatus.Available, LocationId.New(), LocationId.New(), 5);

        var error = Assert.Throws<WmsDomainException>(() => NewCount(moveLine));

        Assert.Equal(WmsErrorCodes.TaskLineInvalid, error.Code);
    }

    [Fact]
    public void ACountLineIsNeverAMoveLine()
    {
        var error = Assert.Throws<WmsDomainException>(
            () => WarehouseTask.CreateMove(
                Tenant, "TSK-20261126-0001", Warehouse, Owner, null, null, null, [CountLine()], null, true, Member, Now));

        Assert.Equal(WmsErrorCodes.TaskLocationInvalid, error.Code);
    }

    [Fact]
    public void ClaimingACountGivesAThirtyMinuteLease()
    {
        var task = NewCount();

        task.Claim(Member, Device, Now);

        Assert.Equal(Now.AddMinutes(30), task.LeaseExpiresAt);
    }

    [Fact]
    public void RecordingACountClosesTheLineAndTheTask()
    {
        var (task, token) = Claimed();
        var lineId = task.Lines[0].Id;

        var line = task.RecordCount(Member, token, Device, lineId, 7, null, Now.AddMinutes(1));

        Assert.Equal(7, line.QuantityCounted);
        Assert.Equal(0, line.QuantityConfirmed);
        Assert.Equal(TaskLineStatus.Done, line.Status);
        var confirmed = Assert.Single(task.Events, e => e.Kind == TaskEventKind.LineConfirmed);
        Assert.Equal(7, confirmed.Quantity);
        Assert.Equal("count", confirmed.Detail);
        Assert.Equal(lineId, confirmed.LineId);
        Assert.Equal(WarehouseTaskStatus.Done, task.Status);
        Assert.Contains(task.Events, e => e.Kind == TaskEventKind.Completed);
    }

    [Fact]
    public void CountingZeroIsValid()
    {
        var (task, token) = Claimed();

        var line = task.RecordCount(Member, token, Device, task.Lines[0].Id, 0, null, Now.AddMinutes(1));

        Assert.Equal(0, line.QuantityCounted);
        Assert.Equal(TaskLineStatus.Done, line.Status);
        Assert.Equal(WarehouseTaskStatus.Done, task.Status);
    }

    [Fact]
    public void ANegativeCountIs422LineInvalid()
    {
        var (task, token) = Claimed();

        var error = Assert.Throws<WmsDomainException>(
            () => task.RecordCount(Member, token, Device, task.Lines[0].Id, -1, null, Now.AddMinutes(1)));

        Assert.Equal(WmsErrorCodes.TaskLineInvalid, error.Code);
        Assert.Equal(TaskLineStatus.Open, task.Lines[0].Status);
    }

    [Fact]
    public void TheScannedLotFillsALineWithoutLot()
    {
        var (task, token) = Claimed();
        var lot = LotId.New();

        var line = task.RecordCount(Member, token, Device, task.Lines[0].Id, 4, lot, Now.AddMinutes(1));

        Assert.Equal(lot, line.LotId);
    }

    [Fact]
    public void AScannedLotOnALineWithAnotherLotIs422LotMismatch()
    {
        var (task, token) = Claimed(CountLine(LotId.New()));

        var error = Assert.Throws<WmsDomainException>(
            () => task.RecordCount(Member, token, Device, task.Lines[0].Id, 4, LotId.New(), Now.AddMinutes(1)));

        Assert.Equal(WmsErrorCodes.TaskLotMismatch, error.Code);
    }

    [Fact]
    public void ConfirmingOrShortingACountLineIsAnInvalidTransition()
    {
        var (task, token) = Claimed();
        var lineId = task.Lines[0].Id;

        var confirm = Assert.Throws<TaskTransitionException>(
            () => task.ConfirmLine(Member, token, Device, lineId, 1, Now.AddMinutes(1)));
        var shorted = Assert.Throws<TaskTransitionException>(
            () => task.ShortLine(Member, token, Device, lineId, 0, Now.AddMinutes(1)));

        Assert.Equal("confirm-line", confirm.ProblemExtensions["action"]);
        Assert.Equal("short-line", shorted.ProblemExtensions["action"]);
        Assert.Equal(TaskLineStatus.Open, task.Lines[0].Status);
    }

    [Fact]
    public void CountingAMoveLineIsAnInvalidTransition()
    {
        var move = WarehouseTask.CreateMove(
            Tenant, "TSK-20261126-0002", Warehouse, Owner, null, null, null,
            [WarehouseTaskLine.Create(Variant, null, StockStatus.Available, LocationId.New(), LocationId.New(), 5)],
            null, true, Member, Now);
        move.Claim(Member, Device, Now);

        var error = Assert.Throws<TaskTransitionException>(
            () => move.RecordCount(Member, move.LeaseToken!.Value, Device, move.Lines[0].Id, 1, null, Now.AddMinutes(1)));

        Assert.Equal("count-line", error.ProblemExtensions["action"]);
    }

    [Fact]
    public void ACountScanBeforeTheReleaseFloorIs422()
    {
        var (task, token) = Claimed();

        var error = Assert.Throws<WmsDomainException>(
            () => task.RecordCount(
                Member, token, Device, task.Lines[0].Id, 1, null, Now.AddMinutes(1), occurredAt: Now.AddMinutes(-10)));

        Assert.Equal(WmsErrorCodes.MovementOccurredAtOutOfRange, error.Code);
    }

    [Fact]
    public void ALostLeaseThrows()
    {
        var (task, _) = Claimed();

        Assert.Throws<InvalidOperationException>(
            () => task.RecordCount(Member, Guid.NewGuid(), Device, task.Lines[0].Id, 1, null, Now.AddMinutes(1)));
    }

    [Fact]
    public void AResumableLeaseResumes()
    {
        var (task, token) = Claimed();
        Assert.True(task.ExpireLease(Now.AddMinutes(31)));
        Assert.Equal(WarehouseTaskStatus.Ready, task.Status);

        task.RecordCount(Member, token, Device, task.Lines[0].Id, 2, null, Now.AddMinutes(32));

        Assert.Contains(task.Events, e => e.Kind == TaskEventKind.LeaseResumed);
        Assert.Equal(WarehouseTaskStatus.Done, task.Status);
    }
}
