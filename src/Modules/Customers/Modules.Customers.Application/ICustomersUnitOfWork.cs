namespace Modules.Customers.Application;

public interface ICustomersUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Abre una transacción explícita sobre la conexión del <c>CustomersDbContext</c> del scope (copia de
    /// <c>ICatalogUnitOfWork.BeginTransactionAsync</c>). La usa la fusión (spec 2026-10-24 §3.2): sus dos <c>SaveChanges</c> en
    /// orden fijo —EF ordena los <c>UPDATE</c> por clave primaria, así que el BSUID, el IGSID y el documento salen del fusionado
    /// en el primero y entran al que queda en el segundo—, la auditoría y el outbox, lo de <see cref="ICustomerMergeStore"/> y
    /// el <c>MoveAsync</c> de <see cref="ICustomerMergeReferences"/> van todos en ella. Sin confirmarla, se revierte al
    /// liberarla: si algo falla, no queda nada a medias.
    /// </summary>
    Task<ICustomersTransaction> BeginTransactionAsync(CancellationToken cancellationToken);
}

public interface ICustomersTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
}
