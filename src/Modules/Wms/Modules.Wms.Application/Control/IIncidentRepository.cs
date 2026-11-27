using Modules.Wms.Domain.Control;

namespace Modules.Wms.Application;

public interface IIncidentRepository
{
    void Add(Incident incident);

    /// <summary>Con evidencia y eventos, sin rastreo: la ficha.</summary>
    Task<Incident?> GetWithEventsAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);

    /// <summary>Si esa Idempotency-Key ya dejó una novedad (deduplicar SyncConflict). Se llama bajo el lock del operario.</summary>
    Task<bool> ExistsWithOperationKeyAsync(Guid tenantId, string operationKey, CancellationToken cancellationToken);

    /// <summary><c>NOV-{yyyyMMdd}-{NNNN}</c> por tenant y día, con lock bloqueante <c>incident-seq:{tenant}</c> de transacción; exige la
    /// transacción de <see cref="IWmsUnitOfWork.BeginTransactionAsync"/>.</summary>
    Task<string> NextNumberAsync(Guid tenantId, DateOnly today, CancellationToken cancellationToken);
}
