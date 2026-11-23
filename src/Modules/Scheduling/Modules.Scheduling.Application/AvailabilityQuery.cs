using Modules.Scheduling.Domain;

namespace Modules.Scheduling.Application;

/// <summary>
/// Todo lo que el motor necesita para calcular los espacios libres de un servicio, ya leído: el motor no accede a datos. Los recursos
/// ofrecidos (<paramref name="OfferedResources"/>, con la sede de cada uno: un bloqueo de sede cierra sólo los de esa sede) son los activos y
/// dentro del alcance de quien pregunta; <paramref name="OnlyResource"/> es
/// el recurso que el cliente eligió, si eligió. Las fechas son días del calendario del tenant y <paramref name="Now"/> es un instante.
/// Con <paramref name="IgnoreOccupancy"/> el motor responde sólo «¿ofrecen las reglas este inicio?»: conserva franjas, cierres, festivos,
/// anticipación y horizonte, y deja de descontar lo que ocupan las sesiones (una llena, o una publicada que el tenant quitó, cuenta como
/// ofrecida). La reserva lo usa para distinguir 422 <c>outside_rules</c> (las reglas no lo ofrecen) de 409 (alguien lo tiene).
/// </summary>
public sealed record AvailabilityQuery(
    Service Service,
    IReadOnlyList<AvailabilityBlock> Blocks,
    IReadOnlyList<Closure> Closures,
    IReadOnlyList<Session> Sessions,
    IReadOnlyDictionary<ResourceId, LocationId> OfferedResources,
    SchedulingSettings Rules,
    IReadOnlySet<DateOnly> Holidays,
    TimeZoneInfo TimeZone,
    DateOnly FirstDay,
    DateOnly LastDay,
    DateTimeOffset Now,
    ResourceId? OnlyResource,
    bool IgnoreOccupancy = false);

/// <summary>Un recurso que puede atender ese inicio y cuántos cupos le quedan. <paramref name="ExistingSessionId"/> es la sesión a la que
/// se suma la reserva; <c>null</c> significa que la reserva abre una nueva.</summary>
public sealed record SlotOption(
    ResourceId ResourceId, AvailabilityBlockId BlockId, int SeatsLeft, SessionId? ExistingSessionId, bool AllowClientChoice);

/// <summary>Un inicio libre. <paramref name="EndsAt"/> es el fin del servicio: el margen ocupa al recurso pero no se le muestra al cliente.</summary>
public sealed record AvailableSlot(DateTimeOffset StartsAt, DateTimeOffset EndsAt, int SeatsLeft, IReadOnlyList<SlotOption> Options);
