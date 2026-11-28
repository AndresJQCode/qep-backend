namespace Modules.Wms.Domain.Inventory;

/// <summary>
/// Los tres documentos que mueven stock en el inventario básico. El valor es el que viaja en <c>LedgerDocument.Type</c>, en
/// <c>reservations.demand_type</c> y en <c>document_stock_states.document_type</c>: no se renombra.
/// </summary>
public static class InventoryDocumentTypes
{
    public const string PurchaseOrder = "PurchaseOrder";
    public const string Order = "Order";
    public const string PosSale = "PosSale";

    public static readonly IReadOnlyList<string> All = [PurchaseOrder, Order, PosSale];

    /// <summary>Un movimiento de uno de estos documentos se deshace desde el documento, no a mano (<c>wms.movement.reverse_from_document</c>).</summary>
    public static bool IsInventoryDocument(string? documentType) =>
        documentType is not null && All.Contains(documentType, StringComparer.Ordinal);
}
