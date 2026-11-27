using Modules.Wms.Domain.Control;

namespace Modules.Wms.Application;

public sealed record IncidentPage(IReadOnlyList<Incident> Items, int TotalCount);

public interface IIncidentRepository
{
    void Add(Incident incident);

    /// <summary>Con evidencia y eventos, rastreada: las transiciones del supervisor la cambian y agregan eventos.</summary>
    Task<Incident?> GetAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);

    /// <summary>Sin rastreo, las más recientes primero. Los filtros nulos no filtran.</summary>
    Task<IncidentPage> ListAsync(
        Guid tenantId,
        Guid? warehouseId,
        IncidentStatus? status,
        IncidentType? type,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>Las novedades <c>Open</c> e <c>InReview</c> de la bodega: lo que el tablero de tareas cuenta.</summary>
    Task<int> CountOpenAsync(Guid tenantId, Guid warehouseId, CancellationToken cancellationToken);

    /// <summary>Con evidencia y eventos, sin rastreo: la ficha.</summary>
    Task<Incident?> GetWithEventsAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);

    /// <summary>Si esa Idempotency-Key ya dejó una novedad (deduplicar SyncConflict). Se llama bajo el lock del operario.</summary>
    Task<bool> ExistsWithOperationKeyAsync(Guid tenantId, string operationKey, CancellationToken cancellationToken);

    /// <summary>Las novedades <c>Open</c> e <c>InReview</c> que ese <c>Count</c> verifica, rastreadas con sus eventos: cuando el
    /// conteo se cierra, el hook las resuelve o las desenlaza.</summary>
    Task<IReadOnlyList<Incident>> ListOpenVerifiedByAsync(Guid tenantId, Guid countTaskId, CancellationToken cancellationToken);

    /// <summary>La <c>CountVariance</c> que ese ajuste cierra, rastreada con sus eventos; <c>null</c> si el ajuste no nació de un
    /// conteo.</summary>
    Task<Incident?> FindByAdjustmentAsync(Guid tenantId, Guid adjustmentId, CancellationToken cancellationToken);

    /// <summary>Las membresías (sin repetir) que reportaron una novedad que ese <c>Count</c> verifica (<c>verification_task_id</c>), en
    /// cualquier estado y con o sin padre: ninguna puede aprobar el ajuste de su <c>CountVariance</c> (DA-47). No se deriva de las
    /// hijas de la <c>CountVariance</c>: una novedad que el supervisor aceptó a mano antes de que el conteo cerrara no es hija de nadie,
    /// y su reportante aprobaría el ajuste que él mismo provocó.</summary>
    Task<IReadOnlyList<Guid>> ListReportersVerifiedByAsync(Guid tenantId, Guid countTaskId, CancellationToken cancellationToken);

    /// <summary>Si esa membresía reportó alguna novedad que ese <c>Count</c> verifica (<c>verification_task_id</c>), en cualquier
    /// estado: nadie cuenta el conteo de una novedad que reportó (decisión del owner, 2026-11-27). Sin rastreo.</summary>
    Task<bool> HasReportedVerifiedByAsync(Guid tenantId, Guid countTaskId, Guid memberId, CancellationToken cancellationToken);

    /// <summary><c>NOV-{yyyyMMdd}-{NNNN}</c> por tenant y día, con lock bloqueante <c>incident-seq:{tenant}</c> de transacción; exige la
    /// transacción de <see cref="IWmsUnitOfWork.BeginTransactionAsync"/>.</summary>
    Task<string> NextNumberAsync(Guid tenantId, DateOnly today, CancellationToken cancellationToken);
}
