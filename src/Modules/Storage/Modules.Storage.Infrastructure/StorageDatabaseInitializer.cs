using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Storage.Infrastructure.Persistence;

namespace Modules.Storage.Infrastructure;

public static class StorageDatabaseInitializer
{
    public static async Task InitializeStorageDatabaseAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<StorageDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
    }
}
