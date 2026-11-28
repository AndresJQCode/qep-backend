namespace Modules.Wms.Domain.Inventory;

/// <summary>
/// La máquina por documento del inventario básico (spec §5.2), pura: decide qué hacer y a qué estado pasa. Cualquier combinación que
/// la tabla no nombra no hace nada y deja el estado donde estaba (el aplicador sólo avanza la versión).
/// </summary>
public static class DocumentStockMachine
{
    /// <summary>El origen que el consumidor atiende; <c>WmsReceipt</c> y <c>WmsPacking</c> los lleva el WMS por su cuenta (§5.1).</summary>
    private const string ManualOrigin = "Manual";

    public static StockDecision Decide(DocumentStockStatus current, long lastVersion, InventoryEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        if (evt.Version <= lastVersion)
        {
            return Stay(current);
        }

        // Parked es una marca del consumidor sobre un documento que todavía no tenía estado: para la máquina, es None.
        var state = current == DocumentStockStatus.Parked ? DocumentStockStatus.None : current;

        return (state, evt.Kind) switch
        {
            (DocumentStockStatus.None, InventoryEventKind.OrderCreated) when !evt.AwaitingWholesaleApproval =>
                new(StockAction.Reserve, DocumentStockStatus.Reserved),
            (DocumentStockStatus.None, InventoryEventKind.OrderWholesaleApproved) =>
                new(StockAction.Reserve, DocumentStockStatus.Reserved),

            (DocumentStockStatus.Reserved, InventoryEventKind.OrderEdited) =>
                new(StockAction.ReplaceReservation, DocumentStockStatus.Reserved),
            (DocumentStockStatus.Reserved, InventoryEventKind.OrderPrepared) when IsManual(evt) =>
                new(StockAction.TakeOut, DocumentStockStatus.TakenOutByPrepare),
            (DocumentStockStatus.Reserved, InventoryEventKind.OrderArchived) when !evt.IsPrepared =>
                new(StockAction.TakeOut, DocumentStockStatus.TakenOutByArchive),
            (DocumentStockStatus.Reserved, InventoryEventKind.OrderCancelled) =>
                new(StockAction.Release, DocumentStockStatus.Released),

            (DocumentStockStatus.TakenOutByPrepare, InventoryEventKind.OrderUnprepared) =>
                new(StockAction.CompensateAndReserve, DocumentStockStatus.Reserved),
            (DocumentStockStatus.TakenOutByPrepare, InventoryEventKind.OrderCancelled) when evt.FromPlatform =>
                new(StockAction.None, DocumentStockStatus.Released),

            (DocumentStockStatus.TakenOutByArchive, InventoryEventKind.OrderUnarchived) when !evt.IsPrepared =>
                new(StockAction.CompensateAndReserve, DocumentStockStatus.Reserved),
            (DocumentStockStatus.TakenOutByArchive, InventoryEventKind.OrderCancelled) when evt.FromPlatform =>
                new(StockAction.None, DocumentStockStatus.Released),

            (DocumentStockStatus.HandedToOutbound, InventoryEventKind.OrderCancelled) =>
                new(StockAction.Release, DocumentStockStatus.Released),

            (DocumentStockStatus.None, InventoryEventKind.PurchaseReceived) when IsManual(evt) =>
                new(StockAction.Receive, DocumentStockStatus.Received),

            (DocumentStockStatus.None, InventoryEventKind.PosCreated) =>
                new(StockAction.Ship, DocumentStockStatus.Shipped),
            (DocumentStockStatus.Shipped, InventoryEventKind.PosVoided) =>
                new(StockAction.Compensate, DocumentStockStatus.Voided),

            _ => Stay(state),
        };
    }

    private static bool IsManual(InventoryEvent evt) => string.Equals(evt.Origin, ManualOrigin, StringComparison.Ordinal);

    private static StockDecision Stay(DocumentStockStatus status) => new(StockAction.None, status);
}
