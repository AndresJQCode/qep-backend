namespace Modules.Wms.Domain.Inventory;

/// <summary>
/// La clave de un saldo: dueño, ubicación, variante, lote, unidad logística y estado. Los ids son <c>Guid</c> blandos a propósito:
/// <c>Inventory</c> no ve <c>Warehousing</c> (WmsLayerTests). <c>OwnerId</c> va en la clave (RNF-13): el stock de dos dueños nunca
/// se mezcla. <c>HandlingUnitId</c> (D-12) es la caja o el palet que contiene el stock; <c>null</c> es stock suelto. Va al final y
/// con default para que los llamadores que no conocen unidades sigan armando claves sueltas.
/// </summary>
public readonly record struct StockKey(
    Guid OwnerId,
    Guid LocationId,
    Guid VariantId,
    Guid? LotId,
    StockStatus Status,
    Guid? HandlingUnitId = null);

/// <summary>
/// Orden total de las claves. Todo el que bloquea o escribe varios saldos lo hace en este orden (RNF-03): dos lotes que tocan las
/// mismas claves las toman en la misma secuencia y no se esperan en círculo. Ubicación, variante, lote, unidad, estado y dueño; el
/// dueño va al final para que el orden sea total.
/// </summary>
public sealed class StockKeyComparer : IComparer<StockKey>
{
    public static StockKeyComparer Instance { get; } = new();

    public int Compare(StockKey x, StockKey y)
    {
        var byLocation = x.LocationId.CompareTo(y.LocationId);
        if (byLocation != 0)
        {
            return byLocation;
        }

        var byVariant = x.VariantId.CompareTo(y.VariantId);
        if (byVariant != 0)
        {
            return byVariant;
        }

        // Nullable.Compare: null es menor que cualquier lote.
        var byLot = Nullable.Compare(x.LotId, y.LotId);
        if (byLot != 0)
        {
            return byLot;
        }

        // La unidad va después del lote (spec §6.3): la suelta (null) primero.
        var byHandlingUnit = Nullable.Compare(x.HandlingUnitId, y.HandlingUnitId);
        if (byHandlingUnit != 0)
        {
            return byHandlingUnit;
        }

        var byStatus = ((int)x.Status).CompareTo((int)y.Status);
        return byStatus != 0 ? byStatus : x.OwnerId.CompareTo(y.OwnerId);
    }
}
