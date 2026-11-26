namespace Modules.Wms.Domain.Inventory;

/// <summary>
/// Una fila del ledger (<c>wms.stock_movements</c>, append-only): doble partida de una sola fila, de <c>From</c> a <c>To</c>.
/// La bodega de cada lado no se guarda: se deduce de la ubicación, que no cambia de bodega. Sin setters públicos y sin <c>DbSet</c>: se escribe por SQL crudo desde el ledger. Un trigger rechaza UPDATE y DELETE.
/// </summary>
public sealed class StockMovement
{
    private StockMovement()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid OwnerId { get; private set; }

    public Guid VariantId { get; private set; }

    public Guid? LotId { get; private set; }

    public Guid? SerialUnitId { get; private set; }

    public Guid FromLocationId { get; private set; }

    public StockStatus FromStatus { get; private set; }

    /// <summary>D-12: la unidad logística de origen; <c>null</c> es stock suelto. Puede ser distinta de la de destino.</summary>
    public Guid? FromHandlingUnitId { get; private set; }

    public Guid ToLocationId { get; private set; }

    public StockStatus ToStatus { get; private set; }

    public Guid? ToHandlingUnitId { get; private set; }

    public decimal Quantity { get; private set; }

    public string? CapturedUom { get; private set; }

    public decimal? CapturedQty { get; private set; }

    public decimal? ConversionFactor { get; private set; }

    public MovementReason Reason { get; private set; }

    public string? DocumentType { get; private set; }

    public Guid? DocumentId { get; private set; }

    public Guid? DocumentLineId { get; private set; }

    public Guid BatchId { get; private set; }

    public string IdempotencyKey { get; private set; } = string.Empty;

    public Guid ActorId { get; private set; }

    public LedgerActorType ActorType { get; private set; }

    public string? DeviceId { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public DateTimeOffset RecordedAt { get; private set; }

    public Guid? ReversesMovementId { get; private set; }

    /// <summary>S4: referencia blanda al original, sin FK (PostgreSQL no admite FK a una tabla particionada salvo por la clave
    /// completa). Va junto con <see cref="ReversesMovementId"/> o ninguno.</summary>
    public DateTimeOffset? ReversesOccurredAt { get; private set; }

    public string? OverrideReason { get; private set; }

    /// <summary>Sólo estructura: <c>quantity &gt; 0</c> y <c>from != to</c>. Las reglas con perfil van en <c>LedgerEntryRules</c>.</summary>
    internal static StockMovement Record(
        Guid id,
        Guid tenantId,
        StockKey from,
        StockKey to,
        decimal quantity,
        string? capturedUom,
        decimal? capturedQty,
        decimal? conversionFactor,
        MovementReason reason,
        string? documentType,
        Guid? documentId,
        Guid? documentLineId,
        Guid batchId,
        string idempotencyKey,
        Guid actorId,
        LedgerActorType actorType,
        string? deviceId,
        DateTimeOffset occurredAt,
        DateTimeOffset recordedAt,
        Guid? reversesMovementId,
        DateTimeOffset? reversesOccurredAt,
        string? overrideReason,
        Guid? serialUnitId)
    {
        if (quantity <= 0)
        {
            throw new WmsDomainException(WmsErrorCodes.MovementQuantityInvalid, "La cantidad del movimiento tiene que ser mayor que cero.");
        }

        if (from == to)
        {
            throw new WmsDomainException(WmsErrorCodes.MovementSameKey, "El origen y el destino del movimiento son la misma clave de saldo.");
        }

        if (reversesMovementId.HasValue != reversesOccurredAt.HasValue)
        {
            throw new ArgumentException("The reversal reference needs both the movement id and its date, or neither.", nameof(reversesOccurredAt));
        }

        return new StockMovement
        {
            Id = id,
            TenantId = tenantId,
            OwnerId = from.OwnerId,
            VariantId = from.VariantId,
            LotId = from.LotId,
            SerialUnitId = serialUnitId,
            FromLocationId = from.LocationId,
            FromStatus = from.Status,
            FromHandlingUnitId = from.HandlingUnitId,
            ToLocationId = to.LocationId,
            ToStatus = to.Status,
            ToHandlingUnitId = to.HandlingUnitId,
            Quantity = quantity,
            CapturedUom = capturedUom,
            CapturedQty = capturedQty,
            ConversionFactor = conversionFactor,
            Reason = reason,
            DocumentType = documentType,
            DocumentId = documentId,
            DocumentLineId = documentLineId,
            BatchId = batchId,
            IdempotencyKey = idempotencyKey,
            ActorId = actorId,
            ActorType = actorType,
            DeviceId = deviceId,
            OccurredAt = occurredAt,
            RecordedAt = recordedAt,
            ReversesMovementId = reversesMovementId,
            ReversesOccurredAt = reversesOccurredAt,
            OverrideReason = overrideReason,
        };
    }
}
