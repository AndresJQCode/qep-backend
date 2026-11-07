namespace Modules.Store.Application;

public interface IStoreUnitOfWork
{
    Task SaveChangesAsync(CancellationToken cancellationToken);

    Task<IStoreTransaction> BeginTransactionAsync(CancellationToken cancellationToken);

    /// <summary>Suelta lo que quedó pendiente de guardar, p. ej. la solicitud que perdió una carrera por <c>clientRequestId</c>.</summary>
    void DiscardPendingChanges();
}

public interface IStoreTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
}
