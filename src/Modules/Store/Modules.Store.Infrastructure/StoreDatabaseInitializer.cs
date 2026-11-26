using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Store.Infrastructure.Persistence;

namespace Modules.Store.Infrastructure;

public static class StoreDatabaseInitializer
{
    public static async Task InitializeStoreDatabaseAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
    }
}
