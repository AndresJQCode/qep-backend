using Modules.Wms.Application;
using Modules.Wms.Domain;
using Modules.Wms.Domain.Control;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.UnitTests.Control;

public sealed class AdjustmentTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Actor = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly OwnerId Owner = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    private static readonly Guid Variant = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static AdjustmentLine Line(decimal delta, LocationId? location = null, StockStatus status = StockStatus.Available) =>
        AdjustmentLine.Create(location ?? LocationId.New(), Variant, null, status, delta, null, null);

    private static Adjustment Proposed(params AdjustmentLine[] lines) =>
        Adjustment.Propose(
            Tenant, "ADJ-20261102-0001", Owner, WarehouseId.New(), AdjustmentReason.CountVariance, null, null,
            lines.Length == 0 ? [Line(5)] : lines, Actor, Now);

    [Fact]
    public void ProposeRejectsZeroDeltaLines()
    {
        var error = Assert.Throws<WmsDomainException>(() => Line(0));

        Assert.Equal(WmsErrorCodes.AdjustmentLineInvalid, error.Code);
    }

    [Fact]
    public void ProposeRequiresBetweenOneAndFiveHundredLines()
    {
        var none = Assert.Throws<WmsDomainException>(() => Adjustment.Propose(
            Tenant, "ADJ-1", Owner, WarehouseId.New(), AdjustmentReason.Loss, null, null, [], Actor, Now));
        var many = Assert.Throws<WmsDomainException>(() => Adjustment.Propose(
            Tenant, "ADJ-1", Owner, WarehouseId.New(), AdjustmentReason.Loss, null, null,
            Enumerable.Range(0, 501).Select(_ => Line(1)).ToList(), Actor, Now));

        Assert.Equal(WmsErrorCodes.AdjustmentLineInvalid, none.Code);
        Assert.Equal(WmsErrorCodes.AdjustmentLineInvalid, many.Code);
    }

    [Fact]
    public void ProposeChecksTheOccurredAtWindow()
    {
        var error = Assert.Throws<WmsDomainException>(() => Adjustment.Propose(
            Tenant, "ADJ-1", Owner, WarehouseId.New(), AdjustmentReason.Found, null, Now.AddDays(-31), [Line(1)], Actor, Now));

        Assert.Equal(WmsErrorCodes.MovementOccurredAtOutOfRange, error.Code);
    }

    [Fact]
    public void PostingRequiresApproval()
    {
        var adjustment = Proposed();

        var error = Assert.Throws<InvalidTransitionException>(() => adjustment.MarkPosted(Guid.NewGuid(), Now));

        Assert.Equal(WmsErrorCodes.DocumentInvalidTransition, error.Code);
        Assert.Equal("Proposed", error.ProblemExtensions["from"]);
        Assert.Equal("post", error.ProblemExtensions["action"]);
        Assert.Equal(AdjustmentStatus.Proposed, adjustment.Status);
    }

    [Fact]
    public void RejectingAPostedAdjustmentIsInvalid()
    {
        var adjustment = Proposed();
        adjustment.Approve(Actor, Now);
        adjustment.MarkPosted(Guid.NewGuid(), Now);

        var error = Assert.Throws<InvalidTransitionException>(() => adjustment.Reject(Actor, "tarde", Now));

        Assert.Equal("Posted", error.ProblemExtensions["from"]);
        Assert.Equal("reject", error.ProblemExtensions["action"]);
    }

    [Fact]
    public void ApprovingTwiceIsInvalid()
    {
        var adjustment = Proposed();
        adjustment.Approve(Actor, Now);

        var error = Assert.Throws<InvalidTransitionException>(() => adjustment.Approve(Actor, Now));

        Assert.Equal("Approved", error.ProblemExtensions["from"]);
        Assert.Equal("approve", error.ProblemExtensions["action"]);
    }

    [Fact]
    public void EveryTransitionBumpsTheVersion()
    {
        var adjustment = Proposed();
        Assert.Equal(1, adjustment.Version);

        adjustment.Approve(Actor, Now);
        Assert.Equal(2, adjustment.Version);

        var batch = Guid.NewGuid();
        adjustment.MarkPosted(batch, Now);
        Assert.Equal(3, adjustment.Version);
        Assert.Equal(batch, adjustment.BatchId);
        Assert.Equal(AdjustmentStatus.Posted, adjustment.Status);

        var rejected = Proposed();
        rejected.Reject(Actor, "no procede", Now);
        Assert.Equal(2, rejected.Version);
        Assert.Equal(AdjustmentStatus.Rejected, rejected.Status);
        Assert.Equal("no procede", rejected.RejectNote);
    }

    [Fact]
    public void RejectingNeedsANote()
    {
        var error = Assert.Throws<WmsDomainException>(() => Proposed().Reject(Actor, " ", Now));

        Assert.Equal(WmsErrorCodes.AdjustmentLineInvalid, error.Code);
    }

    [Fact]
    public void PostingEntriesComeFromAndGoToTheAdjustmentLocationByDeltaSign()
    {
        var warehouse = Warehouse.Create(WarehouseId.New(), Tenant, "BOG", "Bogota", null, Now);
        var adjustmentLocation = warehouse.VirtualLocation(LocationRole.Adjustment);
        var bin = LocationId.New();
        var other = LocationId.New();
        var adjustment = Proposed(Line(10, bin, StockStatus.Available), Line(-4, other, StockStatus.Quarantine));

        var entries = AdjustmentPosting.EntriesFor(adjustment, adjustmentLocation, Now, MovementReason.Adjustment);

        Assert.Equal(2, entries.Count);
        var up = entries[0];
        Assert.Equal(adjustmentLocation.Id.Value, up.From.LocationId);
        Assert.Equal(bin.Value, up.To.LocationId);
        Assert.Equal(10m, up.Quantity);
        Assert.Equal(StockStatus.Available, up.From.Status);
        Assert.Equal(StockStatus.Available, up.To.Status);
        var down = entries[1];
        Assert.Equal(other.Value, down.From.LocationId);
        Assert.Equal(adjustmentLocation.Id.Value, down.To.LocationId);
        Assert.Equal(4m, down.Quantity);
        Assert.Equal(StockStatus.Quarantine, down.From.Status);
        Assert.Equal(StockStatus.Quarantine, down.To.Status);
        Assert.All(entries, entry =>
        {
            Assert.Equal(Owner.Value, entry.From.OwnerId);
            Assert.Equal("Adjustment", entry.Document!.Type);
            Assert.Equal(adjustment.Id, entry.Document.Id);
            Assert.Equal(MovementReason.Adjustment, entry.Reason);
        });
        Assert.Equal(adjustment.Lines[0].Id, entries[0].Document!.LineId);
    }

    [Fact]
    public void ACapturedQuantityTravelsWithItsFactor()
    {
        var warehouse = Warehouse.Create(WarehouseId.New(), Tenant, "BOG", "Bogota", null, Now);
        var line = AdjustmentLine.Create(LocationId.New(), Variant, null, StockStatus.Available, 24m, "CAJA", 2m);
        var adjustment = Proposed(line);

        var entry = AdjustmentPosting.EntriesFor(adjustment, warehouse.VirtualLocation(LocationRole.Adjustment), Now, MovementReason.Adjustment).Single();

        Assert.Equal(new LedgerCapture("CAJA", 2m, 12m), entry.Captured);
    }
}
