using Modules.Wms.Domain.Tasks;

namespace Modules.Wms.Application;

public sealed record WarehouseTaskPage(IReadOnlyList<WarehouseTask> Items, int TotalCount);

public sealed record TaskStatusCount(WarehouseTaskStatus Status, int Count);

/// <summary>Las tareas de bodega (spec 2026-11-26 §6). Todas las lecturas traen las líneas en orden de recorrido. Las que sólo
/// alimentan una pantalla vienen sin rastreo: así no se cuelan en un <c>SaveChangesAsync</c> ni se quedan viejas en el contexto si
/// después se bloquea y se recarga la misma tarea.</summary>
public interface IWarehouseTaskRepository
{
    void Add(WarehouseTask task);

    /// <summary>Con líneas, sin eventos (se agregan sin cargarlos), rastreada.</summary>
    Task<WarehouseTask?> GetAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);

    /// <summary><c>SELECT … FOR UPDATE</c> de la fila, bloqueante; exige la transacción abierta. Devuelve <c>false</c> si no existe en
    /// el tenant. Se llama antes de <see cref="GetAsync"/> y antes de tocar saldos.</summary>
    Task<bool> LockAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);

    /// <summary>Con líneas y eventos, sin rastreo: la ficha del supervisor. Los eventos vienen sin orden: los de una misma transacción
    /// comparten <c>occurred_at</c>, así que quien los muestra ordena por fecha, tipo (orden del enum) e id.</summary>
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

    /// <summary>Sin rastreo.</summary>
    Task<IReadOnlyList<WarehouseTask>> ListInProgressAsync(Guid tenantId, Guid warehouseId, CancellationToken cancellationToken);

    /// <summary>Las publicadas que esperan a esa membresía (<c>Assigned</c>), sin rastreo; la que ya tiene en curso no viene.</summary>
    Task<IReadOnlyList<WarehouseTask>> ListAssignedToAsync(
        Guid tenantId, Guid warehouseId, Guid memberId, CancellationToken cancellationToken);

    /// <summary>La tarea en curso de la membresía, en cualquier bodega, sin rastreo: para cambiarla se bloquea y se carga con
    /// <see cref="GetAsync"/>.</summary>
    Task<WarehouseTask?> FindInProgressOfMemberAsync(Guid tenantId, Guid memberId, CancellationToken cancellationToken);

    /// <summary>La mejor tarea elegible que nadie tenga bloqueada, y la deja bloqueada por esta transacción (exige la de
    /// <see cref="IWmsUnitOfWork.BeginTransactionAsync"/>); <c>null</c> si no hay ninguna. El orden es el de
    /// <see cref="TaskPriority"/> (spec §6.6) calculado en la base con <paramref name="now"/>: primero lo asignado a la membresía,
    /// luego el puntaje, el corte más próximo, la antigüedad en el pool y el id.</summary>
    Task<Guid?> LockNextCandidateAsync(
        Guid tenantId,
        Guid warehouseId,
        Guid memberId,
        IReadOnlyCollection<WarehouseTaskType> types,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary><c>TSK-{yyyyMMdd}-{NNNN}</c>, consecutivo por tenant y día. Toma un lock bloqueante de transacción antes de contar;
    /// exige la transacción de <see cref="IWmsUnitOfWork.BeginTransactionAsync"/> y el lock vive hasta que ella confirme o se
    /// deshaga.</summary>
    Task<string> NextNumberAsync(Guid tenantId, DateOnly today, CancellationToken cancellationToken);
}
