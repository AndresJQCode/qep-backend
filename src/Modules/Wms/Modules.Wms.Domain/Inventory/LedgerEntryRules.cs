namespace Modules.Wms.Domain.Inventory;

/// <summary>Las reglas de un movimiento que dependen del perfil de la variante y del estado de las ubicaciones.</summary>
public static class LedgerEntryRules
{
    public const int MaxPastDays = 30;
    public const int MaxFutureMinutes = 5;

    /// <summary>La holgura de reloj de un celular, la misma del futuro: un escaneo real puede traer la hora atrasada hasta tanto.
    /// </summary>
    public const int ClockToleranceMinutes = MaxFutureMinutes;

    /// <summary>S12: la fecha física no puede ser de hace más de 30 días ni estar más de 5 minutos en el futuro.</summary>
    public static void EnsureOccurredAtInWindow(DateTimeOffset occurredAt, DateTimeOffset now)
    {
        if (occurredAt < now.AddDays(-MaxPastDays) || occurredAt > now.AddMinutes(MaxFutureMinutes))
        {
            throw new WmsDomainException(
                WmsErrorCodes.MovementOccurredAtOutOfRange,
                "La fecha del movimiento tiene que estar entre hace 30 días y dentro de 5 minutos.");
        }
    }

    public static void Validate(
        LedgerEntry entry,
        LedgerVariantRules variant,
        LedgerLocationInfo from,
        LedgerLocationInfo to,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(variant);
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        if (from.Id != entry.From.LocationId || to.Id != entry.To.LocationId)
        {
            throw new ArgumentException("The location info does not match the entry keys.", nameof(from));
        }

        EnsureOccurredAtInWindow(entry.OccurredAt, now);

        if (decimal.Round(entry.Quantity, variant.BaseUomPrecision) != entry.Quantity)
        {
            throw new WmsDomainException(
                WmsErrorCodes.UomPrecisionExceeded,
                $"La cantidad admite como máximo {variant.BaseUomPrecision} decimales para esta variante.");
        }

        EnsureTracking(entry, variant);
        // Una compensación conserva la captura del original sólo para trazarla: la cantidad base ya está en el movimiento y el perfil
        // pudo cambiar después, así que revalidarla dejaría el reverso imposible para siempre.
        if (entry.ReversesMovementId is null)
        {
            EnsureCapture(entry, variant);
        }

        if (from.IsBlocked || to.IsBlocked)
        {
            throw new WmsDomainException(WmsErrorCodes.LocationBlocked, "Una de las ubicaciones está bloqueada.");
        }

        // Una ubicación en conteo sólo admite lo que el propio conteo escribe: el ajuste y su compensación (S20).
        if ((from.IsCounting || to.IsCounting) && entry.Reason is not (MovementReason.Adjustment or MovementReason.Reversal))
        {
            throw new WmsDomainException(WmsErrorCodes.LocationCounting, "Una de las ubicaciones está en conteo.");
        }
    }

    private static void EnsureTracking(LedgerEntry entry, LedgerVariantRules variant)
    {
        switch (variant.Tracking)
        {
            case TrackingPolicy.Lot when entry.From.LotId is null:
                throw new WmsDomainException(WmsErrorCodes.LotRequired, "La variante se controla por lote: el movimiento necesita lote.");
            case TrackingPolicy.None when entry.From.LotId is not null:
                throw new WmsDomainException(WmsErrorCodes.LotNotAllowed, "La variante no se controla por lote: el movimiento no puede traer lote.");
            case TrackingPolicy.Serial when entry.SerialUnitId is null || entry.Quantity != 1m:
                throw new WmsDomainException(WmsErrorCodes.SerialRequired, "La variante se controla por serie: cada movimiento mueve exactamente una unidad con su serie.");
            default:
                break;
        }
    }

    private static void EnsureCapture(LedgerEntry entry, LedgerVariantRules variant)
    {
        var captured = entry.Captured;
        if (captured is null)
        {
            return;
        }

        decimal expected;
        if (string.Equals(captured.Uom, variant.BaseUom, StringComparison.OrdinalIgnoreCase))
        {
            expected = 1m;
        }
        else if (!TryFactor(variant.ConversionFactors, captured.Uom, out expected))
        {
            throw new WmsDomainException(WmsErrorCodes.UomFactorMismatch, "La presentación capturada no existe en el perfil de la variante.");
        }

        if (captured.Factor != expected || captured.Quantity * captured.Factor != entry.Quantity)
        {
            throw new WmsDomainException(WmsErrorCodes.UomFactorMismatch, "El factor o la cantidad capturados no coinciden con el perfil de la variante.");
        }
    }

    // Aunque el diccionario venga sin comparador insensible, la captura se compara sin distinguir mayúsculas.
    private static bool TryFactor(IReadOnlyDictionary<string, decimal> factors, string uom, out decimal factor)
    {
        if (factors.TryGetValue(uom, out factor))
        {
            return true;
        }

        foreach (var (code, value) in factors)
        {
            if (string.Equals(code, uom, StringComparison.OrdinalIgnoreCase))
            {
                factor = value;
                return true;
            }
        }

        factor = 0m;
        return false;
    }
}
