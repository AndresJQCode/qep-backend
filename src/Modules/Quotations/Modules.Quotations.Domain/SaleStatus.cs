namespace Modules.Quotations.Domain;

/// <summary>
/// Una venta nace <see cref="Pending"/> y otra persona la revisa antes de aprobarla: quien
/// convierte la cotización y quien da el visto bueno son roles distintos, y el estado es lo que
/// hace visible ese paso intermedio.
///
/// Las ventas anteriores a esta separación quedaron en <see cref="Approved"/>: se crearon cuando
/// convertir **era** aprobar, y reescribirlas diría que alguien las revisó.
/// </summary>
public enum SaleStatus
{
    Pending,
    Approved
}
