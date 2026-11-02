using Modules.Wms.Application;
using Modules.Wms.Domain.Control;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.UnitTests.Control;

/// <summary>La carga inicial es un solo batch de a lo sumo 500 movimientos porque cada fila es exactamente un movimiento: ni siquiera la
/// contrapartida (<c>ADJUSTMENT</c>) añade uno, porque el movimiento ya la lleva de un extremo.</summary>
public sealed class AdjustmentPostingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static (Adjustment Adjustment, Location Counterpart) Arrange(params decimal[] deltas)
    {
        var warehouse = WarehouseTests.NewWarehouse();
        var bin = warehouse.AddLocation(
            LocationId.New(), null, "A-01", LocationKind.Zone, LocationRole.Storage, true, true, LocationCapacity.None, 0, [], Now);
        var lines = deltas
            .Select(delta => AdjustmentLine.Create(bin.Id, Guid.CreateVersion7(), null, StockStatus.Available, delta, null, null))
            .ToList();
        var adjustment = Adjustment.Propose(
            Guid.CreateVersion7(), "ADJ-20261101-0001", new OwnerId(Guid.CreateVersion7()), warehouse.Id, AdjustmentReason.InitialLoad, null, null, lines,
            Guid.CreateVersion7(), Now);
        return (adjustment, warehouse.VirtualLocation(LocationRole.Adjustment));
    }

    [Fact]
    public void EachInitialLoadLineIsExactlyOneEntryFromTheAdjustmentLocation()
    {
        var (adjustment, counterpart) = Arrange(5m, 7m, 1m);

        var entries = AdjustmentPosting.EntriesFor(adjustment, counterpart, Now, MovementReason.InitialLoad);

        Assert.Equal(adjustment.Lines.Count, entries.Count);
        Assert.All(entries, entry =>
        {
            Assert.Equal(counterpart.Id.Value, entry.From.LocationId);
            Assert.Equal(MovementReason.InitialLoad, entry.Reason);
        });
    }

    [Fact]
    public void ANegativeLineIsStillOneEntryTheOtherWay()
    {
        var (adjustment, counterpart) = Arrange(-3m);

        var entry = Assert.Single(AdjustmentPosting.EntriesFor(adjustment, counterpart, Now, MovementReason.Adjustment));

        Assert.Equal(counterpart.Id.Value, entry.To.LocationId);
        Assert.Equal(3m, entry.Quantity);
    }
}
