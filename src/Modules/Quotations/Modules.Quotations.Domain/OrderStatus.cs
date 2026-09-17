namespace Modules.Quotations.Domain;

/// <summary>
/// Un pedido nace <see cref="Pending"/> y otra persona lo revisa antes de aprobarlo: quien
/// convierte la cotización y quien da el visto bueno son roles distintos, y el estado es lo que
/// hace visible ese paso intermedio.
///
/// Los pedidos anteriores a esta separación quedaron en <see cref="Approved"/>: se crearon cuando
/// convertir **era** aprobar, y reescribirlos diría que alguien los revisó.
///
/// <see cref="Cancelled"/> (spec 2026-09-16) es terminal y se llega desde los otros dos: el pedido
/// no se borra, queda con quién, cuándo y por qué se anuló.
/// </summary>
public enum OrderStatus
{
    Pending,
    Approved,
    Cancelled
}
