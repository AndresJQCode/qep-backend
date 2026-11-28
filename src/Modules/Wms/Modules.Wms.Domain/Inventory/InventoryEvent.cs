namespace Modules.Wms.Domain.Inventory;

/// <summary>Los once eventos de §5.1 que mueven stock. <c>wholesale-cancelled</c> no está: el <c>cancelled</c> ya libera.</summary>
public enum InventoryEventKind
{
    OrderCreated,
    OrderWholesaleApproved,
    OrderEdited,
    OrderPrepared,
    OrderUnprepared,
    OrderCancelled,
    OrderArchived,
    OrderUnarchived,
    PurchaseReceived,
    PosCreated,
    PosVoided,
}

/// <summary>Lo que la máquina necesita de un evento: su tipo y los pocos datos que deciden (el resto, las líneas, las usa el aplicador).</summary>
public sealed record InventoryEvent(
    InventoryEventKind Kind,
    long Version,
    string? Origin,
    bool IsPrepared,
    bool FromPlatform,
    bool AwaitingWholesaleApproval);

/// <summary>Qué hace el aplicador con el documento.</summary>
public enum StockAction
{
    None,
    Reserve,
    ReplaceReservation,
    TakeOut,
    CompensateAndReserve,
    Release,
    Receive,
    Ship,
    Compensate,
}

public sealed record StockDecision(StockAction Action, DocumentStockStatus Next);
