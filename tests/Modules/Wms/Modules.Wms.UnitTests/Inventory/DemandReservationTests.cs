using Modules.Wms.Domain.Inventory;

namespace Modules.Wms.UnitTests.Inventory;

public sealed class DemandReservationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.CreateVersion7();

    private static Reservation Open(decimal quantity = 3m) =>
        Reservation.OpenDemand(
            Tenant, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), quantity,
            InventoryDocumentTypes.Order, Guid.CreateVersion7(), Guid.CreateVersion7(), Now);

    [Fact]
    public void ADemandReservationHasNoLocation()
    {
        var demandId = Guid.CreateVersion7();
        var lineId = Guid.CreateVersion7();
        var warehouseId = Guid.CreateVersion7();

        var reservation = Reservation.OpenDemand(
            Tenant, Guid.CreateVersion7(), warehouseId, Guid.CreateVersion7(), 2.5m, InventoryDocumentTypes.Order, demandId, lineId, Now);

        Assert.Null(reservation.LocationId);
        Assert.Null(reservation.LotId);
        Assert.Equal(ReservationStatus.Open, reservation.Status);
        Assert.Equal(2.5m, reservation.Quantity);
        Assert.Equal(warehouseId, reservation.WarehouseId);
        Assert.Equal("Order", reservation.DemandType);
        Assert.Equal(demandId, reservation.DemandId);
        Assert.Equal(lineId, reservation.DemandLineId);
        Assert.Equal(Now, reservation.CreatedAt);
    }

    [Fact]
    public void ClosingReleasesAndConsumingConsumesOnlyWhatIsOpen()
    {
        var closed = Open();
        closed.Close();
        Assert.Equal(ReservationStatus.Released, closed.Status);

        var consumed = Open();
        consumed.Consume();
        Assert.Equal(ReservationStatus.Consumed, consumed.Status);

        Assert.Throws<InvalidOperationException>(closed.Consume);
        Assert.Throws<InvalidOperationException>(consumed.Close);
    }

    [Fact]
    public void AQuantityThatIsNotPositiveIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Open(0m));
        Assert.Throws<ArgumentOutOfRangeException>(() => Open(-1m));
    }

    [Fact]
    public void TheDocumentTypesAreTheThreeThatMoveStock()
    {
        Assert.Equal(["PurchaseOrder", "Order", "PosSale"], InventoryDocumentTypes.All);
        Assert.True(InventoryDocumentTypes.IsInventoryDocument("Order"));
        Assert.False(InventoryDocumentTypes.IsInventoryDocument("Adjustment"));
        Assert.False(InventoryDocumentTypes.IsInventoryDocument(null));
    }
}
