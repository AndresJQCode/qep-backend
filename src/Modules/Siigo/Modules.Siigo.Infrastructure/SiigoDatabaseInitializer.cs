using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Siigo.Infrastructure.Persistence;

namespace Modules.Siigo.Infrastructure;

public static class SiigoDatabaseInitializer
{
    public static async Task InitializeSiigoDatabaseAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SiigoDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
    }
}
