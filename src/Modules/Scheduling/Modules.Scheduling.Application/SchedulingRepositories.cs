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

    /// <summary>Los recursos de una sede, archivados incluidos, <b>con seguimiento</b>: archivar la sede los archiva y lo guarda en la misma
    /// unidad de trabajo. <see cref="ListAsync"/> lee sin seguimiento y un cambio sobre lo que devuelve no llega a la base.</summary>
    Task<IReadOnlyList<Resource>> ListByLocationForUpdateAsync(Guid tenantId, LocationId locationId, CancellationToken cancellationToken);

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

    /// <summary>Las franjas no archivadas del tenant, con seguimiento de cambios: archivar un recurso o un servicio las edita al sacar
    /// su id. El filtro por id es en memoria, como en <see cref="ListAsync"/>.</summary>
    Task<IReadOnlyList<AvailabilityBlock>> ListActiveForUpdateAsync(Guid tenantId, CancellationToken cancellationToken);

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

    /// <summary>
    /// Las sesiones de esos recursos que se solapan con el rango (empiezan antes de <paramref name="rangeEnd"/> y ocupan hasta después de
    /// <paramref name="rangeStart"/>), sin seguimiento de cambios: es lo que lee el motor de disponibilidad. Con
    /// <paramref name="onlyActive"/> falso entran también las inactivas (una publicada que el tenant quitó sigue siendo una sesión
    /// publicada, aunque no ocupe nada).
    /// </summary>
    Task<IReadOnlyList<Session>> ListOverlappingAsync(
        Guid tenantId,
        IReadOnlyCollection<ResourceId> resourceIds,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd,
        bool onlyActive,
        CancellationToken cancellationToken);

    /// <summary>Alguna cita de la sesión, en cualquier estado: la llave foránea impide borrarla mientras exista una.</summary>
    Task<bool> HasAppointmentsAsync(Guid tenantId, SessionId id, CancellationToken cancellationToken);

    /// <summary>Alguna cita de la sesión que no esté cancelada: la que aún cuenta para quien la tiene.</summary>
    Task<bool> HasLiveAppointmentsAsync(Guid tenantId, SessionId id, CancellationToken cancellationToken);

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

public interface IAppointmentRepository
{
    /// <summary>La cita del tenant con su historial, con seguimiento de cambios: cada transición la edita.</summary>
    Task<Appointment?> FindAsync(Guid tenantId, AppointmentId id, CancellationToken cancellationToken);

    /// <summary>
    /// La cita como está en la base ahora, después de que un guardado perdió la carrera por la versión (<c>RequestConcurrencyException</c>).
    /// Descarta lo que el contexto tenía pendiente y sin seguimiento previo: la transacción del guardado ya se deshizo, así que esos cambios
    /// no existen. Sólo para decidir qué le cuenta un camino sin sesión a quien llama; un handler HTTP relee con la pantalla.
    /// </summary>
    Task<Appointment?> FindFreshAsync(Guid tenantId, AppointmentId id, CancellationToken cancellationToken);

    /// <summary>
    /// Descarta todo lo que el contexto sigue con seguimiento. Un trabajo del agente comparte un contexto entre sus escrituras: la que falla
    /// (un guardado que perdió, una excepción a mitad de camino) no puede dejar sus entidades pendientes, o la siguiente las vuelve a guardar.
    /// </summary>
    void Discard();

    /// <summary>
    /// Vuelve a leer los campos de la cita desde la base. Se llama con el candado del recurso ya tomado: lo que otra petición escribió entre
    /// la lectura y el candado (una cancelación, un cambio de hora) se ve acá y no después, cuando liberar un cupo que ya se liberó fallaría
    /// con un <c>InvalidOperationException</c> o, peor, descontaría dos veces.
    /// </summary>
    Task RefreshAsync(Appointment appointment, CancellationToken cancellationToken);

    /// <summary>La cita que el tenant ya reservó con esa llave de idempotencia, con su historial.</summary>
    Task<Appointment?> FindByClientRequestAsync(Guid tenantId, string clientRequestId, CancellationToken cancellationToken);

    /// <summary>
    /// <c>SELECT … FOR UPDATE</c> de las filas de esos recursos, de una vez y en orden de id para que dos reservas que piden los mismos
    /// recursos no se crucen. Debe correr dentro de una transacción de <see cref="ISchedulingUnitOfWork.BeginTransactionAsync"/>: el candado
    /// dura hasta el commit y es lo que serializa las reservas, los cambios de hora y las cancelaciones de un mismo recurso.
    /// </summary>
    Task LockResourcesAsync(Guid tenantId, IReadOnlyCollection<ResourceId> resourceIds, CancellationToken cancellationToken);

    /// <summary>
    /// Las citas <c>Pending</c> o <c>Confirmed</c> de esos recursos que empiezan después de <paramref name="now"/>, con su historial y con
    /// seguimiento de cambios: es lo que archivar una sede cancela. Una cita que ya empezó no entra.
    /// </summary>
    Task<IReadOnlyList<Appointment>> ListFutureLiveAsync(
        Guid tenantId, IReadOnlyCollection<ResourceId> resourceIds, DateTimeOffset now, CancellationToken cancellationToken);

    void Add(Appointment appointment);
}

/// <summary>Lo que el escritorio lee de las citas en una sola pasada (ver <see cref="IAppointmentReadRepository.ReadDeskAsync"/>).</summary>
public sealed record DeskSnapshot(
    int DayAppointments,
    int Attended,
    int NoShow,
    int PendingTotal,
    IReadOnlyList<Appointment> Pending,
    int UnmarkedTotal,
    IReadOnlyList<Appointment> Unmarked,
    IReadOnlyList<Appointment> Upcoming);

/// <summary>
/// Las lecturas de citas del calendario y del escritorio, sin seguimiento de cambios. Aparte de <see cref="IAppointmentRepository"/>, que
/// es de escritura y trae cada cita con su historial. Toda consulta filtra por el alcance <b>en la base</b>: un miembro sin «ver todas las
/// agendas» nunca recibe una fila de otro recurso, ni siquiera para descartarla después.
/// </summary>
public interface IAppointmentReadRepository
{
    /// <summary>Las citas que ocupan lugar (no canceladas) y se solapan con el rango, de esos recursos y dentro del alcance, por inicio y luego por id.</summary>
    Task<IReadOnlyList<Appointment>> ListInRangeAsync(
        Guid tenantId,
        ResourceScope scope,
        IReadOnlyCollection<ResourceId> resourceIds,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd,
        CancellationToken cancellationToken);

    /// <summary>
    /// The next appointments of one customer: <c>Pending</c> or <c>Confirmed</c> that start at or after <paramref name="now"/>, by start and
    /// then id, at most <paramref name="take"/>. Without the resource scope on purpose: it serves the channel, where the customer is the filter.
    /// </summary>
    Task<IReadOnlyList<Appointment>> ListUpcomingByCustomerAsync(
        Guid tenantId, Guid customerId, DateTimeOffset now, int take, CancellationToken cancellationToken);

    /// <summary>
    /// Los contadores y las colas del escritorio dentro del alcance. Las del día son las que <b>empiezan</b> en <c>[dayStart, dayEnd)</c>;
    /// «por confirmar» y «sin marcar» (<see cref="DeskRules.Unmarked"/>) son colas de cualquier día. Cada lista trae como mucho
    /// <paramref name="take"/> filas y su total viaja aparte.
    /// </summary>
    Task<DeskSnapshot> ReadDeskAsync(
        Guid tenantId,
        ResourceScope scope,
        DateTimeOffset dayStart,
        DateTimeOffset dayEnd,
        DateTimeOffset now,
        int take,
        CancellationToken cancellationToken);
}
