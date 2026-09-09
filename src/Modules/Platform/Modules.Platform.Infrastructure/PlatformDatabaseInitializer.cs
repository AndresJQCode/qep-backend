using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Platform.Infrastructure.Persistence;

namespace Modules.Platform.Infrastructure;

public static class PlatformDatabaseInitializer
{
    public static async Task InitializePlatformDatabaseAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
    }
}
