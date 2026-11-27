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

    /// <summary>Las membresías (sin repetir) que reportaron las novedades que esa <c>CountVariance</c> cerró: ninguna puede aprobar el
    /// ajuste (DA-47).</summary>
    Task<IReadOnlyList<Guid>> ListReportersOfChildrenAsync(Guid tenantId, Guid parentIncidentId, CancellationToken cancellationToken);

    /// <summary><c>NOV-{yyyyMMdd}-{NNNN}</c> por tenant y día, con lock bloqueante <c>incident-seq:{tenant}</c> de transacción; exige la
    /// transacción de <see cref="IWmsUnitOfWork.BeginTransactionAsync"/>.</summary>
    Task<string> NextNumberAsync(Guid tenantId, DateOnly today, CancellationToken cancellationToken);
}
