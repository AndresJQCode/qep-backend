namespace Modules.Scheduling.Application;

// Lecturas BFF de la Agenda (plan 2026-11-21, Tarea 10). La forma la decide lo que la pantalla dibuja: el cliente no cuenta recursos, no
// suma citas ni resuelve nombres, y nada de lo que viaja depende de qué tan grande sea el tenant.

/// <summary>Un tramo del día de un recurso, en instantes UTC: la pantalla lo pinta con la zona horaria de <see cref="CalendarDto.TimeZone"/>.</summary>
public sealed record TimeWindowDto(DateTimeOffset Start, DateTimeOffset End);

/// <param name="Reason">Nulo si quien lo creó no dio motivo: la pantalla pinta «Bloqueado» a secas.</param>
public sealed record CalendarClosureDto(Guid Id, DateTimeOffset Start, DateTimeOffset End, string? Reason);

/// <summary>
/// Una cita en la grilla, con el nombre del cliente y del servicio ya resueltos: dibujar una celda no consulta dos listas más.
/// <c>Booked</c> y <c>Capacity</c> son los de su sesión (una clase de cupo 5 con 3 inscritos se pinta «3/5» en cada una de sus filas).
/// </summary>
/// <param name="Status"><c>Pending</c>, <c>Confirmed</c>, <c>Completed</c> o <c>NoShow</c>. Las canceladas no viajan: liberaron su lugar.</param>
/// <param name="Source"><c>BackOffice</c> o <c>Channel</c>.</param>
public sealed record CalendarAppointmentDto(
    Guid Id,
    DateTimeOffset Start,
    DateTimeOffset End,
    string Status,
    string CustomerName,
    string ServiceName,
    string Source,
    Guid SessionId,
    int Booked,
    int Capacity);

/// <summary>Una columna del calendario. Los conteos los calcula el servidor con las mismas reglas del motor de disponibilidad.</summary>
/// <param name="AppointmentCount">Las citas que se dibujan en la columna: las que ocupan lugar (no canceladas) en el rango pedido.</param>
/// <param name="FreeCount">Los inicios en que el recurso todavía admite una reserva en el rango, de cualquiera de los servicios que ofrece
/// (un inicio que sirve a dos servicios cuenta una vez). Aplica anticipación mínima y horizonte, así que lo ya pasado no cuenta.</param>
/// <param name="OpenWindows">Cuándo abre el recurso: las franjas del rango ya sin festivos y fundidas cuando se tocan, para pintar el fondo
/// de la columna. Los bloqueos no se descuentan acá; viajan aparte en <c>Closures</c>.</param>
public sealed record CalendarResourceDto(
    Guid Id,
    string DisplayName,
    Guid LocationId,
    int AppointmentCount,
    int FreeCount,
    IReadOnlyList<TimeWindowDto> OpenWindows,
    IReadOnlyList<CalendarClosureDto> Closures,
    IReadOnlyList<CalendarAppointmentDto> Appointments);

/// <summary>
/// <c>GET calendar</c>: una sola respuesta, ya filtrada por el alcance de quien pide.
/// </summary>
/// <param name="Layout"><c>Personal</c> (una sede y un recurso activos del tenant) o <c>Consolidated</c>: lo decide el servidor, la
/// pantalla no cuenta recursos. Es el del tenant, no el del alcance de quien mira: un médico con una sola agenda, en un consultorio con
/// varias, sigue viendo «Consolidated».</param>
/// <param name="TimeZone">La del tenant. Los instantes viajan en UTC.</param>
/// <param name="From">Primer día pedido, ya resuelto (un <c>date</c> llega como <c>from = to</c>); calendario del tenant, inclusive.</param>
/// <param name="To">Último día pedido, inclusive.</param>
/// <param name="Resources">Los recursos del alcance (o los pedidos) por nombre, aun sin citas ni franjas: una columna vacía se dibuja vacía.</param>
public sealed record CalendarDto(
    string Layout, string TimeZone, DateOnly From, DateOnly To, IReadOnlyList<CalendarResourceDto> Resources);

/// <summary>
/// Los seis contadores del escritorio. Viajan siempre completos, en cero incluso: un contador que desaparece obliga a la pantalla a conocer
/// la lista de estados para dibujar el que falta.
/// </summary>
/// <param name="Appointments">Las citas del día que ocupan lugar (no canceladas).</param>
/// <param name="Pending">Las citas por confirmar de cualquier día: es el total de la cola <c>pending</c>, aunque la lista se acote.</param>
/// <param name="Unmarked">Las confirmadas que ya terminaron sin marcar si asistieron, de cualquier día: el total de la cola <c>unmarked</c>.</param>
/// <param name="Attended">Las atendidas del día.</param>
/// <param name="NoShow">Las del día en que el cliente no llegó.</param>
/// <param name="Free">Los inicios libres del día entre los recursos del alcance, contados como <see cref="CalendarResourceDto.FreeCount"/>.</param>
public sealed record DeskCountersDto(int Appointments, int Pending, int Unmarked, int Attended, int NoShow, int Free);

/// <summary>Una fila de las colas del escritorio, con todo resuelto para dibujarla sin consultar la cita.</summary>
/// <param name="RequestedAgo">Minutos enteros desde que se pidió la cita, con el reloj del servidor: la pantalla sólo los formatea
/// («hace 2 h») y no depende de que el reloj del navegador coincida con el del servidor. Nunca es negativo.</param>
public sealed record DeskItemDto(
    Guid AppointmentId,
    string CustomerName,
    string ServiceName,
    string ResourceName,
    DateTimeOffset Start,
    string Source,
    int RequestedAgo);

/// <summary>
/// <c>GET desk</c>: la vista con que la secretaria abre el día. Con alcance, todo es sólo de lo que el miembro ve. Las tres listas se acotan
/// (<see cref="GetDeskHandler.QueueLimit"/>) para que un tenant con meses sin marcar no responda miles de filas; los contadores siguen
/// siendo los totales.
/// </summary>
/// <param name="Date">El día de los contadores del día y de <c>Upcoming</c>: el pedido o, sin <c>date</c>, hoy en la zona del tenant.</param>
/// <param name="Pending">Las citas por confirmar, la que lleva más tiempo esperando primero.</param>
/// <param name="Unmarked">Las confirmadas ya terminadas sin marcar, la más reciente primero: es la que se acuerdan de marcar.</param>
/// <param name="Upcoming">Las citas del día que aún no terminan, por hora de inicio.</param>
public sealed record DeskDto(
    DateOnly Date,
    DeskCountersDto Counters,
    IReadOnlyList<DeskItemDto> Pending,
    IReadOnlyList<DeskItemDto> Unmarked,
    IReadOnlyList<DeskItemDto> Upcoming);
