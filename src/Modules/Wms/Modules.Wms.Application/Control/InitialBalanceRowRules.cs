using System.Globalization;
using Modules.Wms.Domain;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.Application;

/// <summary>Lo que el handler sabe de una fila y la regla pura no puede mirar: el lote que ya existe, si la clave ya tiene saldo y las
/// fechas con las que las filas anteriores de la hoja dejarían a cada lote nuevo.</summary>
public sealed record InitialBalanceRowContext(
    Lot? ExistingLot = null,
    bool StockExists = false,
    IDictionary<(Guid VariantId, string LotCode), DateOnly?>? NewLotDates = null);

/// <summary>La fila ya clasificada y la cantidad que la persona escribió en su presentación (<c>3</c> de <c>3 CAJA</c>): el preview la
/// guarda para que el commit arme la captura del movimiento sin volver a leer el perfil.</summary>
public sealed record ClassifiedInitialBalanceRow(InitialBalancePreviewRowDto Row, decimal? CapturedQuantity);

/// <summary>
/// Las reglas de una fila de la carga inicial (RF-024), puras: el handler resuelve códigos, ubicaciones y perfiles y se las pasa
/// ya resueltos. Nunca asigna por aproximación: una coincidencia que no es exacta no es la variante.
/// </summary>
public static class InitialBalanceRowRules
{
    /// <summary>Lo que cabe en <c>numeric(18,6)</c> (12 enteros). Más que esto el commit moriría en la base con un 500.</summary>
    private const decimal MaxQuantity = 1_000_000_000_000m;

    private static readonly string[] DateFormats = ["yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "yyyy/MM/dd"];

    public static InitialBalancePreviewRowDto Classify(
        InitialBalanceSheetRow row,
        IReadOnlyList<WmsVariantSnapshot> candidates,
        Location? location,
        LedgerVariantRules? variant,
        bool tracksExpiry,
        ISet<(Guid VariantId, Guid LocationId, string? LotCode)> seenKeys) =>
        ClassifyDetailed(row, candidates, location, variant, tracksExpiry, seenKeys).Row;

    public static ClassifiedInitialBalanceRow ClassifyDetailed(
        InitialBalanceSheetRow row,
        IReadOnlyList<WmsVariantSnapshot> candidates,
        Location? location,
        LedgerVariantRules? variant,
        bool tracksExpiry,
        ISet<(Guid VariantId, Guid LocationId, string? LotCode)> seenKeys,
        InitialBalanceRowContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(seenKeys);

        var issues = new List<string>();
        var code = row.Code?.Trim() ?? string.Empty;
        var locationCode = row.Location?.Trim() ?? string.Empty;
        var lotCode = string.IsNullOrWhiteSpace(row.Lot) ? null : row.Lot.Trim();

        var resolved = ResolveVariant(code, candidates, issues);

        // Una virtual no es existencia real, y una que no admite almacenar no puede guardar saldo: para la carga no existen.
        if (location is null || location.IsVirtual || !location.AllowsStorage)
        {
            issues.Add(WmsErrorCodes.ImportUnknownLocation);
            location = null;
        }
        else if (location.Status == LocationStatus.Blocked)
        {
            // El ledger lo rechazaría al commitear con estos mismos códigos: mejor decirlo en el preview.
            issues.Add(WmsErrorCodes.LocationBlocked);
        }
        else if (location.Status == LocationStatus.Counting)
        {
            issues.Add(WmsErrorCodes.LocationCounting);
        }

        var expiration = ParseExpiration(row.Expiration, lotCode, tracksExpiry, issues, resolved is not null && variant is not null);
        CheckLot(lotCode, variant, resolved is not null, issues);
        if (resolved is not null && lotCode is not null)
        {
            CheckLotExpiry(resolved.VariantId, lotCode, expiration, context, issues);
        }

        var (quantity, uom, captured) = ConvertQuantity(row, variant, issues);

        if (resolved is not null && location is not null && !seenKeys.Add((resolved.VariantId, location.Id.Value, lotCode)))
        {
            issues.Add(WmsErrorCodes.ImportDuplicateRow);
        }

        // Cargar dos veces el mismo archivo duplicaría el saldo (el upsert suma): la clave con saldo no entra. Pendiente del owner (P-12).
        if (context?.StockExists == true && location is not null)
        {
            issues.Add(WmsErrorCodes.ImportStockExists);
        }

        return new ClassifiedInitialBalanceRow(
            new InitialBalancePreviewRowDto(
                row.Row,
                code,
                resolved?.VariantId,
                resolved?.ProductName,
                locationCode,
                location?.Id.Value,
                lotCode,
                expiration,
                quantity,
                uom,
                issues),
            captured);
    }

    /// <summary>La variante cuyo código es igual carácter por carácter, si es una sola. El handler la usa para elegir el perfil antes de
    /// clasificar la fila.</summary>
    public static WmsVariantSnapshot? FindExact(string code, IReadOnlyList<WmsVariantSnapshot> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var exact = candidates.Where(candidate => string.Equals(candidate.Code, code, StringComparison.Ordinal)).ToList();
        return exact.Count == 1 ? exact[0] : null;
    }

    private static WmsVariantSnapshot? ResolveVariant(string code, IReadOnlyList<WmsVariantSnapshot> candidates, List<string> issues)
    {
        if (FindExact(code, candidates) is { } found)
        {
            return found;
        }

        var exact = candidates.Where(candidate => string.Equals(candidate.Code, code, StringComparison.Ordinal)).ToList();

        // Sin exacta: dos o más que sólo difieren en mayúsculas son ambiguas, y una sola inexacta no se adopta.
        var insensitive = candidates.Where(candidate => string.Equals(candidate.Code, code, StringComparison.OrdinalIgnoreCase)).ToList();
        issues.Add(exact.Count > 1 || insensitive.Count > 1 ? WmsErrorCodes.ImportAmbiguousCode : WmsErrorCodes.ImportUnknownCode);
        return null;
    }

    // La fecha de un lote la fija quien lo crea. Una fila que dice otra, ya sea frente a un lote que existe o frente a una fila anterior de
    // la misma hoja que lo creará, perdería su fecha en silencio y el stock quedaría bajo una que nadie aprobó (FEFO y alertas).
    private static void CheckLotExpiry(
        Guid variantId, string lotCode, DateOnly? expiration, InitialBalanceRowContext? context, List<string> issues)
    {
        if (context is null)
        {
            return;
        }

        if (context.ExistingLot is { } existing)
        {
            if (expiration is not null && existing.ExpirationDate != expiration)
            {
                issues.Add(WmsErrorCodes.ImportLotExpiryConflict);
            }

            return;
        }

        if (context.NewLotDates is not { } dates)
        {
            return;
        }

        var key = (variantId, lotCode);
        if (!dates.TryGetValue(key, out var previous))
        {
            dates[key] = expiration;
        }
        else if (expiration is not null && previous != expiration)
        {
            issues.Add(WmsErrorCodes.ImportLotExpiryConflict);
        }
    }

    private static void CheckLot(string? lotCode, LedgerVariantRules? variant, bool variantResolved, List<string> issues)
    {
        if (lotCode?.Length > Lot.CodeMaxLength)
        {
            issues.Add(WmsErrorCodes.LotCodeInvalid);
        }

        if (!variantResolved || variant is null)
        {
            return;
        }

        switch (variant.Tracking)
        {
            case TrackingPolicy.Lot when lotCode is null:
                issues.Add(WmsErrorCodes.LotRequired);
                break;
            case TrackingPolicy.None when lotCode is not null:
                issues.Add(WmsErrorCodes.LotNotAllowed);
                break;
            case TrackingPolicy.Serial:
                // Cada unidad de una variante por serie lleva su número: una hoja de cantidades no puede traerlo.
                issues.Add(WmsErrorCodes.SerialRequired);
                break;
            default:
                break;
        }
    }

    private static DateOnly? ParseExpiration(string? raw, string? lotCode, bool tracksExpiry, List<string> issues, bool profileKnown)
    {
        var text = raw?.Trim();
        DateOnly? parsed = null;
        if (!string.IsNullOrEmpty(text))
        {
            if (DateOnly.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                parsed = date;
            }
            else
            {
                // Una fecha que no se entiende no se descarta en silencio: la hoja se corrige.
                issues.Add(WmsErrorCodes.LotExpiryRequired);
                return null;
            }
        }

        if (lotCode is not null && tracksExpiry && profileKnown && parsed is null)
        {
            issues.Add(WmsErrorCodes.LotExpiryRequired);
        }

        return parsed;
    }

    private static (decimal Quantity, string? Uom, decimal? Captured) ConvertQuantity(
        InitialBalanceSheetRow row, LedgerVariantRules? variant, List<string> issues)
    {
        var uom = string.IsNullOrWhiteSpace(row.Uom) ? null : row.Uom.Trim();
        if (!TryParseQuantity(row.Quantity, out var written) || written <= 0 || written >= MaxQuantity)
        {
            issues.Add(WmsErrorCodes.MovementQuantityInvalid);
            return (0m, uom, null);
        }

        if (variant is null)
        {
            return (written, uom, uom is null ? null : written);
        }

        var factor = 1m;
        if (uom is not null && !string.Equals(uom, variant.BaseUom, StringComparison.OrdinalIgnoreCase))
        {
            var found = variant.ConversionFactors
                .Where(pair => string.Equals(pair.Key, uom, StringComparison.OrdinalIgnoreCase))
                .Select(pair => (decimal?)pair.Value)
                .FirstOrDefault();
            if (found is not { } conversion)
            {
                issues.Add(WmsErrorCodes.UomFactorMismatch);
                return (written, uom, null);
            }

            factor = conversion;
        }

        // written < MaxQuantity y factor <= 2^31 cabe en un decimal, pero no en numeric(18,6): se compara contra el tope en base.
        var baseQuantity = written * factor;
        if (baseQuantity >= MaxQuantity)
        {
            issues.Add(WmsErrorCodes.MovementQuantityInvalid);
            return (0m, uom, null);
        }

        if (decimal.Round(baseQuantity, variant.BaseUomPrecision) != baseQuantity)
        {
            issues.Add(WmsErrorCodes.UomPrecisionExceeded);
        }

        return (baseQuantity, uom, uom is null ? null : written);
    }

    // Sin separador de miles: «1,5» es uno y medio, no mil quinientos. Se acepta la coma decimal sólo cuando no hay punto.
    private static bool TryParseQuantity(string? raw, out decimal value)
    {
        value = 0m;
        var text = raw?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        const NumberStyles style = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;
        if (decimal.TryParse(text, style, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        return !text.Contains('.', StringComparison.Ordinal)
            && decimal.TryParse(text.Replace(',', '.'), style, CultureInfo.InvariantCulture, out value);
    }
}
