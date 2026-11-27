using Modules.Wms.Domain.Tasks;

namespace Modules.Wms.Application;

public sealed record WarehouseTaskPage(IReadOnlyList<WarehouseTask> Items, int TotalCount);

public sealed record TaskStatusCount(WarehouseTaskStatus Status, int Count);

public sealed record TaskTypeCount(WarehouseTaskType Type, int Count);

/// <summary>Una tarea con el lease vencido, con su tenant para cargarla.</summary>
public sealed record ExpiredTaskLease(Guid TenantId, Guid TaskId);

/// <summary>Una tarea abierta con asignado, lo justo para el tablero.</summary>
public sealed record AssignedTaskRef(Guid TaskId, string Number, WarehouseTaskType Type, Guid AssignedMemberId);

/// <summary>Las tareas de bodega (spec 2026-11-26 §6). Todo se pide por tenant, salvo <see cref="LockExpiredAsync"/>, que es del
/// barrido. Todas las lecturas traen las líneas en orden de recorrido. Las que sólo alimentan una pantalla vienen sin rastreo: así
/// no se cuelan en un <c>SaveChangesAsync</c> ni se quedan viejas en el contexto si después se bloquea y se recarga la misma
/// tarea.</summary>
public interface IWarehouseTaskRepository
{
    void Add(WarehouseTask task);

    /// <summary>Con líneas, sin eventos (se agregan sin cargarlos), rastreada.</summary>
    Task<WarehouseTask?> GetAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);

    /// <summary><c>SELECT … FOR UPDATE</c> de la fila, bloqueante; exige la transacción abierta. Devuelve <c>false</c> si no existe en
    /// el tenant. Se llama antes de <see cref="GetAsync"/> y antes de tocar saldos.</summary>
    Task<bool> LockAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);

    /// <summary>Con líneas y eventos, sin rastreo: la ficha del supervisor. Los eventos vienen sin orden: los de una misma transacción
    /// comparten fecha, así que quien los muestra ordena por <c>recorded_at</c>, tipo (orden del enum) e id.</summary>
    Task<WarehouseTask?> GetWithEventsAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);

    /// <summary>Sin rastreo, las más recientes primero.</summary>
    Task<WarehouseTaskPage> ListAsync(
        Guid tenantId,
        Guid? warehouseId,
        WarehouseTaskStatus? status,
        WarehouseTaskType? type,
        Guid? assignedMemberId,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>Sólo los estados con tareas: quien arma el tablero completa los ceros.</summary>
    Task<IReadOnlyList<TaskStatusCount>> CountByStatusAsync(Guid tenantId, Guid warehouseId, CancellationToken cancellationToken);

    /// <summary>Las abiertas (ni <c>Done</c> ni <c>Cancelled</c>) por tipo. Sólo los tipos que tienen: quien arma el tablero completa
    /// los ceros.</summary>
    Task<IReadOnlyList<TaskTypeCount>> CountOpenByTypeAsync(Guid tenantId, Guid warehouseId, CancellationToken cancellationToken);

    /// <summary>Las <c>Planned</c> y <c>Assigned</c> de la bodega que tienen asignado, sin rastreo y en orden de número. Todas: el
    /// tablero decide cuáles quedaron huérfanas.</summary>
    Task<IReadOnlyList<AssignedTaskRef>> ListAssignedOpenAsync(Guid tenantId, Guid warehouseId, CancellationToken cancellationToken);

    /// <summary>La fecha del último reclamo (evento <c>Claimed</c>) de cada tarea; la que nunca se reclamó no viene.</summary>
    Task<IReadOnlyDictionary<Guid, DateTimeOffset>> FindClaimedAtAsync(
        Guid tenantId, IReadOnlyCollection<Guid> taskIds, CancellationToken cancellationToken);

    /// <summary>El número de cada tarea del tenant que esté entre <paramref name="taskIds"/>; la que no existe no viene.</summary>
    Task<IReadOnlyDictionary<Guid, string>> FindNumbersAsync(
        Guid tenantId, IReadOnlyCollection<Guid> taskIds, CancellationToken cancellationToken);

    /// <summary>El destino (<c>ToLocationId</c>) de cada línea que lo tiene, sin cargar la tarea ni rastrearla: lo que la resolución
    /// <c>AcceptIntoStock</c> de una novedad necesita de la línea de origen. La línea sin destino o que no existe no viene.</summary>
    Task<IReadOnlyDictionary<Guid, Guid>> FindLineDestinationsAsync(
        Guid tenantId, IReadOnlyCollection<Guid> lineIds, CancellationToken cancellationToken);

    /// <summary>Sin rastreo.</summary>
    Task<IReadOnlyList<WarehouseTask>> ListInProgressAsync(Guid tenantId, Guid warehouseId, CancellationToken cancellationToken);

    /// <summary>Las publicadas que esperan a esa membresía (<c>Assigned</c>), sin rastreo; la que ya tiene en curso no viene.</summary>
    Task<IReadOnlyList<WarehouseTask>> ListAssignedToAsync(
        Guid tenantId, Guid warehouseId, Guid memberId, CancellationToken cancellationToken);

    /// <summary>La tarea en curso de la membresía, en cualquier bodega, sin rastreo: para cambiarla se bloquea y se carga con
    /// <see cref="GetAsync"/>.</summary>
    Task<WarehouseTask?> FindInProgressOfMemberAsync(Guid tenantId, Guid memberId, CancellationToken cancellationToken);

    /// <summary>Si la membresía reclamó alguna vez esa tarea (un hecho <c>Claimed</c> suyo en la historia), sin cargarla. Decide si una
    /// acción con el lease perdido deja novedad.</summary>
    Task<bool> HasMemberClaimedAsync(Guid tenantId, Guid taskId, Guid memberId, CancellationToken cancellationToken);

    /// <summary>La mejor tarea elegible que nadie tenga bloqueada, y la deja bloqueada por esta transacción (exige la de
    /// <see cref="IWmsUnitOfWork.BeginTransactionAsync"/>); <c>null</c> si no hay ninguna. El orden es el de
    /// <see cref="TaskPriority"/> (spec §6.6) calculado en la base con <paramref name="now"/>: primero lo asignado a la membresía,
    /// luego el puntaje, el corte más próximo, la antigüedad en el pool y el id. No ofrece la que la membresía saltó hace menos de
    /// <see cref="TaskTypePolicy.SkipCooldown"/>, salvo que la tenga asignada.</summary>
    Task<Guid?> LockNextCandidateAsync(
        Guid tenantId,
        Guid warehouseId,
        Guid memberId,
        IReadOnlyCollection<WarehouseTaskType> types,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>Hasta <paramref name="limit"/> tareas en curso, de cualquier tenant, cuyo lease venció antes de
    /// <paramref name="now"/>, las más vencidas primero; quedan bloqueadas por esta transacción (exige la de
    /// <see cref="IWmsUnitOfWork.BeginTransactionAsync"/>) y las que otra tiene bloqueadas se saltan.</summary>
    Task<IReadOnlyList<ExpiredTaskLease>> LockExpiredAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken);

    /// <summary><c>TSK-{yyyyMMdd}-{NNNN}</c>, consecutivo por tenant y día. Toma un lock bloqueante de transacción antes de contar;
    /// exige la transacción de <see cref="IWmsUnitOfWork.BeginTransactionAsync"/> y el lock vive hasta que ella confirme o se
    /// deshaga.</summary>
    Task<string> NextNumberAsync(Guid tenantId, DateOnly today, CancellationToken cancellationToken);
}
