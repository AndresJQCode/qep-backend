namespace Modules.Quotations.Domain;

/// <summary>
/// Un pedido nace <see cref="Pending"/> y otra persona lo revisa antes de aprobarlo: quien
/// convierte la cotización y quien da el visto bueno son roles distintos, y el estado es lo que
/// hace visible ese paso intermedio.
///
/// Los pedidos anteriores a esta separación quedaron en <see cref="Approved"/>: se crearon cuando
/// convertir **era** aprobar, y reescribirlos diría que alguien los revisó.
/// </summary>
public enum OrderStatus
{
    Pending,
    Approved
}
