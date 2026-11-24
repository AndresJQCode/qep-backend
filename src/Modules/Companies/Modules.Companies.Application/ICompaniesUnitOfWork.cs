namespace Modules.Companies.Application;

public interface ICompaniesUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Abre una transacción explícita sobre la conexión del módulo; <see cref="SaveChangesAsync"/> se suma a ella. Hace falta
    /// para <see cref="LockProvisioningAsync"/>.
    /// </summary>
    Task<ICompaniesTransaction> BeginTransactionAsync(CancellationToken cancellationToken);

    /// <summary>
    /// <c>pg_advisory_xact_lock(CompanyProvisioningLock.Namespace, hashtext('{tenantId}'))</c>. Exige una transacción abierta con
    /// <see cref="BeginTransactionAsync"/>: el candado se suelta con ella. Serializa dos altas del sistema sobre el mismo tenant.
    /// </summary>
    Task LockProvisioningAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>
/// Se confirma con <see cref="CommitAsync"/>; disponerla sin commit hace rollback, que además suelta los candados que haya tomado.
/// </summary>
public interface ICompaniesTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
}
