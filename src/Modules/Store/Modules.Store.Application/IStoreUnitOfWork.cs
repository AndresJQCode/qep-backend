namespace Modules.Store.Application;

public interface IStoreUnitOfWork
{
    Task SaveChangesAsync(CancellationToken cancellationToken);

    Task<IStoreTransaction> BeginTransactionAsync(CancellationToken cancellationToken);
}

public interface IStoreTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
}
