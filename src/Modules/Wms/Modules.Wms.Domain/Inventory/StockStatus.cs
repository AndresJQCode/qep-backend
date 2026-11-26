namespace Modules.Wms.Domain.Inventory;

/// <summary>Estado del saldo en una ubicación. Sólo <c>Available</c> cuenta como disponible (<c>wms.v_stock_rows</c>).</summary>
public enum StockStatus
{
    Available,
    Received,
    Quarantine,
    Blocked,
    Damaged,
    InTransit,
}
