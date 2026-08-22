using Modules.Storage.Application;

namespace Modules.Storage.Infrastructure.Persistence;

internal sealed class StorageUnitOfWork(StorageDbContext dbContext) : IStorageUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken) =>
        dbContext.SaveChangesAsync(cancellationToken);
}
