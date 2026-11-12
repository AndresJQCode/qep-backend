namespace Modules.Store.Application;

public interface IStoreUnitOfWork
{
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
