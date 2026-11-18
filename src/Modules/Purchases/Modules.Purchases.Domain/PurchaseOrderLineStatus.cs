namespace Modules.Purchases.Domain;

/// <summary>
/// Cómo quedó una línea frente a lo que confirmó el proveedor. No se guarda: sale de cantidades y costos
/// (<see cref="PurchaseOrderLine.Status"/>).
/// </summary>
public enum PurchaseOrderLineStatus
{
    /// <summary>Todavía sin confirmar.</summary>
    Pending,

    /// <summary>Confirmada con la misma cantidad y el mismo costo pedidos.</summary>
    Unchanged,

    /// <summary>Confirmada con otra cantidad o con otro costo.</summary>
    Changed,

    /// <summary>Confirmada con cantidad 0: el proveedor no la despacha.</summary>
    Rejected,

    /// <summary>
    /// No se pidió: el mayorista la agregó al aprobar el pedido de una orden vinculada. Sin cantidad pedida, llega ya
    /// confirmada.
    /// </summary>
    Added,
}
