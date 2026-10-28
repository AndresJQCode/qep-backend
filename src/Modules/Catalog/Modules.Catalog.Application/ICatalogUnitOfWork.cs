namespace Modules.Catalog.Application;

public interface ICatalogUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Abre una transacción explícita sobre la conexión del módulo. La usa el PUT de producto para
    /// que la segunda comprobación de uso de las variantes que borra (<see cref="IVariantUsageChecker"/>)
    /// y el borrado vayan en la misma transacción (spec 2026-10-20 §3.3). Sin confirmarla, se
    /// revierte al liberarla.
    /// </summary>
    Task<ICatalogTransaction> BeginTransactionAsync(CancellationToken cancellationToken);
}

public interface ICatalogTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
}
