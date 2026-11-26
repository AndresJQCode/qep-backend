namespace Modules.Wms.Domain.Inventory;

public sealed record LedgerActor(Guid Id, LedgerActorType Type, string? DeviceId);

/// <summary>Cómo lo capturó la persona: <c>3 CAJA</c> con factor 12. El ledger guarda siempre la cantidad en unidad base.</summary>
public sealed record LedgerCapture(string Uom, decimal Quantity, decimal Factor);

public sealed record LedgerDocument(string Type, Guid Id, Guid? LineId);

/// <summary>
/// Un movimiento pedido al ledger: de <see cref="From"/> a <see cref="To"/>, en unidad base. La estructura se valida al
/// construirlo (cantidad positiva, claves distintas, misma variante, dueño y lote); las reglas con perfil y ubicaciones van en
/// <see cref="LedgerEntryRules"/>. La unidad logística no tiene que coincidir entre extremos, como el estado (spec §6.1): recibir
/// dentro de un palet, almacenarlo entero o sacar sueltas cambian la unidad.
/// </summary>
public sealed record LedgerEntry
{
    public LedgerEntry(
        StockKey from,
        StockKey to,
        decimal quantity,
        LedgerCapture? captured,
        MovementReason reason,
        LedgerDocument? document,
        DateTimeOffset occurredAt,
        Guid? reversesMovementId = null,
        DateTimeOffset? reversesOccurredAt = null,
        string? overrideReason = null,
        Guid? serialUnitId = null)
    {
        if (quantity <= 0)
        {
            throw new WmsDomainException(WmsErrorCodes.MovementQuantityInvalid, "La cantidad del movimiento tiene que ser mayor que cero.");
        }

        if (from == to)
        {
            throw new WmsDomainException(WmsErrorCodes.MovementSameKey, "El origen y el destino del movimiento son la misma clave de saldo.");
        }

        if (from.VariantId != to.VariantId || from.OwnerId != to.OwnerId || from.LotId != to.LotId)
        {
            throw new WmsDomainException(
                WmsErrorCodes.MovementSameKey,
                "El movimiento tiene que conservar la variante, el dueño y el lote: sólo cambian la ubicación y el estado.");
        }

        if (reversesMovementId.HasValue != reversesOccurredAt.HasValue)
        {
            throw new ArgumentException("The reversal reference needs both the movement id and its date, or neither.", nameof(reversesOccurredAt));
        }

        From = from;
        To = to;
        Quantity = quantity;
        Captured = captured;
        Reason = reason;
        Document = document;
        // timestamptz sólo acepta offset cero en Npgsql: un occurredAt con -05:00 del cliente sería un 500 en la base.
        OccurredAt = occurredAt.ToUniversalTime();
        ReversesMovementId = reversesMovementId;
        ReversesOccurredAt = reversesOccurredAt?.ToUniversalTime();
        OverrideReason = overrideReason;
        SerialUnitId = serialUnitId;
    }

    public StockKey From { get; }

    public StockKey To { get; }

    public decimal Quantity { get; }

    public LedgerCapture? Captured { get; }

    public MovementReason Reason { get; }

    public LedgerDocument? Document { get; }

    public DateTimeOffset OccurredAt { get; }

    public Guid? ReversesMovementId { get; }

    /// <summary>S4: la fecha del original, para ubicar su partición sin FK.</summary>
    public DateTimeOffset? ReversesOccurredAt { get; }

    public string? OverrideReason { get; }

    public Guid? SerialUnitId { get; }

    /// <summary>La compensación (RF-025): el mismo movimiento al revés, apuntando al original con su fecha.</summary>
    public static LedgerEntry Reverse(
        LedgerEntry original,
        Guid originalMovementId,
        DateTimeOffset originalOccurredAt,
        DateTimeOffset occurredAt,
        string? overrideReason)
    {
        ArgumentNullException.ThrowIfNull(original);

        return new LedgerEntry(
            original.To,
            original.From,
            original.Quantity,
            original.Captured,
            MovementReason.Reversal,
            original.Document,
            occurredAt,
            originalMovementId,
            originalOccurredAt,
            overrideReason,
            original.SerialUnitId);
    }
}
