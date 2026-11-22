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
