namespace Modules.Purchases.Domain;

/// <summary>
/// Lo que el estado de la orden permite hacer (<see cref="PurchaseOrder.AllowedActions"/>). El orden de los miembros es
/// el del contrato, y la pantalla pinta los botones en ese orden.
/// </summary>
public enum PurchaseOrderAction
{
    Edit,
    Delete,
    Send,
    Confirm,
    Receive,
    Cancel,
}
