using Modules.Wms.Domain.Control;

namespace Modules.Wms.Application;

public sealed record AdjustmentPage(IReadOnlyList<Adjustment> Items, int TotalCount);

public interface IAdjustmentRepository
{
    /// <summary>Con sus líneas y rastreado: el agregado cambia de estado.</summary>
    Task<Adjustment?> GetAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);

    void Add(Adjustment adjustment);

    /// <summary>Con sus líneas, sin rastreo, los más recientes primero.</summary>
    Task<AdjustmentPage> ListAsync(
        Guid tenantId, AdjustmentStatus? status, Guid? warehouseId, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary><c>ADJ-{yyyyMMdd}-{NNNN}</c>, consecutivo por tenant y día. Toma un lock bloqueante de transacción antes de contar, así
    /// que dos propuestas simultáneas numeran en serie; exige la transacción de <see cref="IWmsUnitOfWork.BeginTransactionAsync"/> y
    /// el lock vive hasta que ella confirme o se deshaga.</summary>
    Task<string> NextNumberAsync(Guid tenantId, DateOnly today, CancellationToken cancellationToken);
}
