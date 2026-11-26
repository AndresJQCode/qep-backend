namespace Modules.Purchases.Domain;

/// <summary>
/// Ciclo de una orden de compra. Se guarda por nombre: el orden de los miembros no es contrato, pero
/// <c>Draft → Sent → Confirmed → Received</c> es el camino normal y <c>Cancelled</c> cierra desde cualquiera de los tres
/// primeros.
/// </summary>
public enum PurchaseOrderStatus
{
    Draft,
    Sent,
    Confirmed,
    Received,
    Cancelled,
}
