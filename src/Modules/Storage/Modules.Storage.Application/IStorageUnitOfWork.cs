namespace Modules.Storage.Application;

public interface IStorageUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
