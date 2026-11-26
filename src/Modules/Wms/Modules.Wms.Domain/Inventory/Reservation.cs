namespace Modules.Wms.Domain.Inventory;

/// <summary>Reserva de stock para una demanda. Sólo la forma persistida: no hay factorías públicas hasta la fase 3.</summary>
public sealed class Reservation
{
    private Reservation()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid OwnerId { get; private set; }

    public Guid VariantId { get; private set; }

    public Guid WarehouseId { get; private set; }

    public Guid LocationId { get; private set; }

    public LotId? LotId { get; private set; }

    public decimal Quantity { get; private set; }

    public string DemandType { get; private set; } = string.Empty;

    public Guid DemandId { get; private set; }

    public Guid? DemandLineId { get; private set; }

    public ReservationStatus Status { get; private set; }

    public DateTimeOffset? ExpiresAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
}
