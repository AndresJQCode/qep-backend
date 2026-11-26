namespace Modules.Wms.Domain.Inventory;

/// <summary>Cómo se identifica el stock de una variante: sin rastreo, por lote o por serie.</summary>
public enum TrackingPolicy
{
    None,
    Lot,
    Serial,
}
