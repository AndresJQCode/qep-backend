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

    /// <summary>Todos, archivados incluidos, por nombre y luego por id.</summary>
    Task<IReadOnlyList<Service>> ListAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Alguna cita <c>Pending</c> o <c>Confirmed</c> de ese servicio que empiece después de <paramref name="now"/>.</summary>
    Task<bool> HasFutureAppointmentsAsync(Guid tenantId, ServiceId id, DateTimeOffset now, CancellationToken cancellationToken);

    void Add(Service service);
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
