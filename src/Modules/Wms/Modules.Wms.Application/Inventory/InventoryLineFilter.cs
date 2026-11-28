using BuildingBlocks.Domain.Catalog;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.Application;

/// <summary>Por qué una línea no se mueve (spec §5.6). Se loguea y se cuenta; el resto del documento sigue.</summary>
public enum SkipReason
{
    /// <summary>La línea de compra no tiene variante (vinculada al mayorista sin enlazar): <c>Received</c> es final, esperar no la resuelve.</summary>
    NoVariant,

    /// <summary>Catalog no encuentra la variante y no tiene perfil: el ledger lanzaría <c>ProfileVariantUnknown</c> para siempre.</summary>
    UnknownVariant,

    /// <summary>Una línea de servicio no produce nada.</summary>
    Service,

    /// <summary>El inventario básico no conoce lotes ni series.</summary>
    LotOrSerial,

    /// <summary>La cantidad trae más decimales de los que admite el perfil de la variante.</summary>
    Precision,
}

public sealed record SkippedLine(InventoryEventLine Line, SkipReason Reason);

/// <summary>
/// Aparta, antes del ledger, las líneas que no se pueden mover: el ledger tumba el lote entero con la primera entrada mala, así que
/// una línea imposible dejaría todo el documento trabado (spec §5.2, hallazgo 2).
/// </summary>
public static class InventoryLineFilter
{
    public static (IReadOnlyList<InventoryEventLine> Movable, IReadOnlyList<SkippedLine> Skipped) Split(
        IReadOnlyList<InventoryEventLine> lines,
        IReadOnlyDictionary<Guid, WmsVariantSnapshot> snapshots,
        IReadOnlyDictionary<Guid, VariantLogisticsProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(profiles);

        var movable = new List<InventoryEventLine>();
        var skipped = new List<SkippedLine>();
        foreach (var line in lines)
        {
            if (Reason(line, snapshots, profiles) is { } reason)
            {
                skipped.Add(new SkippedLine(line, reason));
            }
            else
            {
                movable.Add(line);
            }
        }

        return (movable, skipped);
    }

    private static SkipReason? Reason(
        InventoryEventLine line,
        IReadOnlyDictionary<Guid, WmsVariantSnapshot> snapshots,
        IReadOnlyDictionary<Guid, VariantLogisticsProfile> profiles)
    {
        if (line.VariantId is not { } variantId)
        {
            return SkipReason.NoVariant;
        }

        snapshots.TryGetValue(variantId, out var snapshot);
        profiles.TryGetValue(variantId, out var profile);

        // El tipo de un pedido viaja en el evento (congelado); el de POS y compras lo da el catálogo.
        var kind = line.Kind ?? snapshot?.Kind;
        if (kind == ProductKind.Service)
        {
            return SkipReason.Service;
        }

        if (snapshot is null && profile is null)
        {
            return SkipReason.UnknownVariant;
        }

        if (profile is not null && profile.Tracking != TrackingPolicy.None)
        {
            return SkipReason.LotOrSerial;
        }

        var precision = profile?.BaseUomPrecision ?? VariantLogisticsProfile.DefaultPrecisionFor(snapshot!.SalesUnit);
        return Math.Round(line.Quantity, precision) != line.Quantity ? SkipReason.Precision : null;
    }
}
