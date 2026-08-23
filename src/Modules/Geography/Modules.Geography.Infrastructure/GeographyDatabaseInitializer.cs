using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Geography.Infrastructure.Persistence;
using Modules.Geography.Infrastructure.Seed;

namespace Modules.Geography.Infrastructure;

public static class GeographyDatabaseInitializer
{
    public static async Task InitializeGeographyDatabaseAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GeographyDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);

        var seeder = scope.ServiceProvider.GetRequiredService<GeographySeeder>();
        await seeder.SeedAsync(cancellationToken);
    }
}
