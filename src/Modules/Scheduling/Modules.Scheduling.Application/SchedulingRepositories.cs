using Modules.Scheduling.Domain;

namespace Modules.Scheduling.Application;

// Cada repositorio nace con el caso de uso que lo usa (plan 2026-11-21, corrección 11). Todo método lleva el tenant: una lectura nunca
// cruza de tenant. Un `Find` por id no lleva el alcance de recursos porque el handler necesita distinguir 404 (no existe en el tenant) de
// 403 (existe pero no es de su alcance); las listas sí lo filtran en la consulta.

public interface ILocationRepository
{
    Task<Location?> FindAsync(Guid tenantId, LocationId id, CancellationToken cancellationToken);

    Task<Location?> FindMainAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Todas, archivadas incluidas (la pantalla las pinta «(archivada)»): la principal primero, luego por nombre.</summary>
    Task<IReadOnlyList<Location>> ListAsync(Guid tenantId, CancellationToken cancellationToken);

    void Add(Location location);
}

public interface IResourceRepository
{
    Task<Resource?> FindAsync(Guid tenantId, ResourceId id, CancellationToken cancellationToken);

    /// <summary>Los que existen en el tenant entre esos ids, en cualquier orden y archivados incluidos: una franja guarda ids sin llave
    /// foránea y quien la guarda los revisa contra esto (plan, corrección 13).</summary>
    Task<IReadOnlyList<Resource>> FindManyAsync(Guid tenantId, IReadOnlyCollection<ResourceId> ids, CancellationToken cancellationToken);

    /// <summary>Los recursos del alcance, archivados incluidos, por nombre y luego por id.</summary>
    Task<IReadOnlyList<Resource>> ListAsync(Guid tenantId, ResourceScope scope, CancellationToken cancellationToken);

    /// <summary>Los recursos vinculados a una membresía: es de donde sale el alcance de quien no tiene «ver todas las agendas».</summary>
    Task<IReadOnlyList<Guid>> ListIdsByMemberAsync(Guid tenantId, Guid memberId, CancellationToken cancellationToken);

    /// <summary>Si ya hay otro recurso de la sede vinculado a esa membresía (índice único por sede y miembro).</summary>
    Task<bool> MemberIsLinkedAsync(
        Guid tenantId, LocationId locationId, Guid memberId, ResourceId? except, CancellationToken cancellationToken);

    /// <summary>Alguna cita <c>Pending</c> o <c>Confirmed</c> que empiece después de <paramref name="now"/>.</summary>
    Task<bool> HasFutureAppointmentsAsync(Guid tenantId, ResourceId id, DateTimeOffset now, CancellationToken cancellationToken);

    void Add(Resource resource);
}

public interface IServiceRepository
{
    Task<Service?> FindAsync(Guid tenantId, ServiceId id, CancellationToken cancellationToken);

    /// <summary>Los que existen en el tenant entre esos ids, en cualquier orden y archivados incluidos.</summary>
    Task<IReadOnlyList<Service>> FindManyAsync(Guid tenantId, IReadOnlyCollection<ServiceId> ids, CancellationToken cancellationToken);

    /// <summary>Todos, archivados incluidos, por nombre y luego por id.</summary>
    Task<IReadOnlyList<Service>> ListAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Alguna cita <c>Pending</c> o <c>Confirmed</c> de ese servicio que empiece después de <paramref name="now"/>.</summary>
    Task<bool> HasFutureAppointmentsAsync(Guid tenantId, ServiceId id, DateTimeOffset now, CancellationToken cancellationToken);

    void Add(Service service);
}

public interface IAvailabilityBlockRepository
{
    Task<AvailabilityBlock?> FindAsync(Guid tenantId, AvailabilityBlockId id, CancellationToken cancellationToken);

    /// <summary>Las franjas que abren al menos uno de los recursos del alcance (todas con alcance «todos»), archivadas incluidas, por
    /// nombre y luego por id. Los ids de recursos van en una columna: el filtro es en memoria (plan, corrección 13).</summary>
    Task<IReadOnlyList<AvailabilityBlock>> ListAsync(Guid tenantId, ResourceScope scope, CancellationToken cancellationToken);

    void Add(AvailabilityBlock block);
}

public interface IClosureRepository
{
    Task<Closure?> FindAsync(Guid tenantId, ClosureId id, CancellationToken cancellationToken);

    /// <summary>Los bloqueos que aún no terminan en <paramref name="after"/> y que el alcance puede ver: los de sus recursos y los de
    /// toda la sede, que le cierran la agenda a todos. Por inicio y luego por id.</summary>
    Task<IReadOnlyList<Closure>> ListAsync(Guid tenantId, ResourceScope scope, DateTimeOffset after, CancellationToken cancellationToken);

    /// <summary>Alguna cita <c>Pending</c> o <c>Confirmed</c> que se solape con el rango en ese recurso o, con <paramref name="resourceId"/>
    /// nulo, en cualquier recurso de la sede (spec D-12).</summary>
    Task<bool> HasActiveAppointmentsAsync(
        Guid tenantId,
        LocationId locationId,
        ResourceId? resourceId,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeFinish,
        CancellationToken cancellationToken);

    void Add(Closure closure);

    void Remove(Closure closure);
}

public interface ISessionRepository
{
    Task<Session?> FindAsync(Guid tenantId, SessionId id, CancellationToken cancellationToken);

    /// <summary>La sesión de ese recurso, servicio e inicio, activa o no: es la clave única de la tabla.</summary>
    Task<Session?> FindByKeyAsync(
        Guid tenantId, ResourceId resourceId, ServiceId serviceId, DateTimeOffset startsAt, CancellationToken cancellationToken);

    /// <summary>Alguna cita de la sesión, en cualquier estado: la llave foránea impide borrarla mientras exista una.</summary>
    Task<bool> HasAppointmentsAsync(Guid tenantId, SessionId id, CancellationToken cancellationToken);

    void Add(Session session);

    void Remove(Session session);
}

public interface ISchedulingSettingsRepository
{
    /// <summary>La fila única del tenant; el aprovisionamiento del primer acceso la crea antes de leerla.</summary>
    Task<SchedulingSettings?> FindAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>Lo que el <c>GET setup</c> cuenta: sólo lo no archivado.</summary>
public sealed record SchedulingSetupSnapshot(
    bool HasService, bool HasResource, bool HasBlock, int ActiveLocations, int ActiveResources);

public interface ISchedulingSetupReader
{
    Task<SchedulingSetupSnapshot> ReadAsync(Guid tenantId, CancellationToken cancellationToken);
}
