namespace Modules.Scheduling.Application;

/// <summary>
/// El cliente de la cita en la pantalla de «Nueva cita»: lo justo para elegirlo y para mostrarlo en la cita. El teléfono es nulo si no lo
/// dio; no se inventa uno.
/// </summary>
public sealed record AppointmentCustomerDto(Guid Id, string Name, string? Phone);

/// <param name="SeatsLeft">Los cupos libres de este recurso a esa hora.</param>
/// <param name="SessionId">La sesión a la que se suma la reserva; nulo significa que la reserva abre una nueva. Sólo sirve para pintar
/// «ya hay gente reservada»: reservar no la necesita, el servidor la vuelve a resolver.</param>
public sealed record AvailabilityOptionDto(Guid ResourceId, string DisplayName, int SeatsLeft, Guid? SessionId);

/// <param name="SeatsLeft">La suma de los cupos libres de todos los recursos que pueden atender ese inicio: la pantalla no suma.</param>
/// <param name="EndsAt">El fin del servicio. El margen entre citas ocupa al recurso pero no se muestra.</param>
public sealed record AvailabilitySlotDto(
    DateTimeOffset StartsAt, DateTimeOffset EndsAt, int SeatsLeft, IReadOnlyList<AvailabilityOptionDto> Resources);

/// <summary>
/// <c>GET availability</c>. <c>TimeZone</c> es la del tenant: los instantes viajan en UTC y la pantalla los pinta con ella. Una lista
/// vacía es «no hay espacios» (no un error): el rango puede quedar fuera del horizonte o de las franjas.
/// </summary>
public sealed record AvailabilityDto(string TimeZone, IReadOnlyList<AvailabilitySlotDto> Slots);

/// <param name="Action">El nombre de <c>AppointmentAction</c>: <c>Created</c>, <c>Confirmed</c>, <c>Cancelled</c>, <c>Attended</c>, <c>NoShow</c>,
/// <c>Rescheduled</c> o <c>ReminderSent</c>.</param>
/// <param name="ActorName">Quién lo hizo, ya resuelto: la pantalla no consulta Personas. Nulo cuando lo hizo el sistema o el cliente por un canal.</param>
/// <param name="Detail">El motivo de una cancelación, si lo hubo.</param>
/// <param name="ByChannel">Verdadero cuando la línea la escribió el agente de IA por WhatsApp (D-17). La pantalla pinta «Cliente por WhatsApp» con
/// este campo y no lo deduce del origen de la cita: un cambio de hora por chat sobre una cita de back office, o cualquier cambio por chat,
/// salía «Sistema». Nunca lo pone una escritura por HTTP ni el recordatorio; las filas anteriores a la columna quedan en falso.</param>
public sealed record AppointmentEventDto(string Action, DateTimeOffset At, string? ActorName, string? Detail, bool ByChannel);

/// <summary>
/// La cita tal como la pantalla de detalle la dibuja, con los nombres ya resueltos (recurso, servicio, cliente): quien la pinta no consulta
/// tres listas para mostrar una fila. Los enums viajan con su nombre. <c>History</c> viaja completo y en orden cronológico: es lo que
/// pinta la tarjeta «Historial». Cada transición responde con esta misma forma, ya con su nueva <c>Version</c>.
/// </summary>
/// <param name="Status"><c>Pending</c>, <c>Confirmed</c>, <c>Cancelled</c>, <c>Completed</c> o <c>NoShow</c>.</param>
/// <param name="Source"><c>BackOffice</c> o <c>Channel</c>.</param>
/// <param name="CustomerPhone">Nulo si el cliente no dio teléfono: sin él no hay recordatorio por WhatsApp.</param>
/// <param name="CreatedAt">Cuándo se pidió: el escritorio ordena la cola de pendientes por este instante.</param>
public sealed record AppointmentDto(
    Guid Id,
    string Status,
    string Source,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    Guid LocationId,
    Guid ResourceId,
    string ResourceName,
    Guid ServiceId,
    string ServiceName,
    Guid CustomerId,
    string CustomerName,
    string? CustomerPhone,
    Guid SessionId,
    string? Note,
    string? CancelReason,
    Guid? ConversationId,
    DateTimeOffset? ReminderSentAt,
    DateTimeOffset CreatedAt,
    long Version,
    IReadOnlyList<AppointmentEventDto> History);
