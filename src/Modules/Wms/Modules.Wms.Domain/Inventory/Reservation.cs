namespace Modules.Wms.Domain.Inventory;

/// <summary>
/// Reserva de stock para una demanda, en dos niveles sobre la misma tabla (inventario básico §5.4). La <b>reserva de demanda</b> es
/// por bodega, variante y dueño, <b>sin ubicación</b>: no toca <c>StockBalance.Reserved</c>, así que nunca frena un movimiento. La
/// <b>asignación</b> (T5) lleva <c>LocationId</c> y es la que suma en el saldo.
/// </summary>
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

    /// <summary>Nulo en la reserva de demanda; con valor en la asignación por ubicación (T5).</summary>
    public Guid? LocationId { get; private set; }

    public LotId? LotId { get; private set; }

    public decimal Quantity { get; private set; }

    public string DemandType { get; private set; } = string.Empty;

    public Guid DemandId { get; private set; }

    public Guid? DemandLineId { get; private set; }

    public ReservationStatus Status { get; private set; }

    public DateTimeOffset? ExpiresAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Abre la reserva de demanda de una línea de documento. Sin ubicación y sin lote.</summary>
    public static Reservation OpenDemand(
        Guid tenantId,
        Guid ownerId,
        Guid warehouseId,
        Guid variantId,
        decimal quantity,
        string demandType,
        Guid demandId,
        Guid demandLineId,
        DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(quantity, 0m);
        ArgumentException.ThrowIfNullOrWhiteSpace(demandType);

        return new Reservation
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            OwnerId = ownerId,
            WarehouseId = warehouseId,
            VariantId = variantId,
            LocationId = null,
            LotId = null,
            Quantity = quantity,
            DemandType = demandType,
            DemandId = demandId,
            DemandLineId = demandLineId,
            Status = ReservationStatus.Open,
            CreatedAt = now,
        };
    }

    /// <summary>La libera: el documento se editó, se anuló o ya no hay nada que apartar.</summary>
    public void Close() => Transition(ReservationStatus.Released);

    /// <summary>La consume: la mercancía salió y la reserva cumplió su trabajo.</summary>
    public void Consume() => Transition(ReservationStatus.Consumed);

    private void Transition(ReservationStatus next)
    {
        if (Status != ReservationStatus.Open)
        {
            throw new InvalidOperationException($"Only an open reservation can become {next}; this one is {Status}.");
        }

        Status = next;
    }
}
