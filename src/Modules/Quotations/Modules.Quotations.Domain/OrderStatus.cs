namespace Modules.Quotations.Domain;

/// <summary>
/// Un pedido nace <see cref="Pending"/> y otra persona lo revisa antes de aprobarlo: quien
/// convierte la cotización y quien da el visto bueno son roles distintos, y el estado es lo que
/// hace visible ese paso intermedio.
///
/// Los pedidos anteriores a esta separación quedaron en <see cref="Approved"/>: se crearon cuando
/// convertir **era** aprobar, y reescribirlos diría que alguien los revisó.
///
/// <see cref="Cancelled"/> (spec 2026-09-16) es terminal y se llega desde <see cref="Pending"/> y <see cref="Approved"/>: un
/// pedido <see cref="Invoiced"/> no se anula, primero se revierte la facturación. El pedido no se
/// borra, queda con quién, cuándo y por qué se anuló.
///
/// <see cref="Invoiced"/> (spec 2026-10-05) sólo se alcanza desde <see cref="Approved"/> y deja
/// constancia de que la factura se hizo fuera de QEP; revertirlo lo devuelve a <see cref="Approved"/>.
/// </summary>
public enum OrderStatus
{
    Pending,
    Approved,
    Cancelled,
    Invoiced
}
