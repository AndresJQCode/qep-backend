using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Pricing.Application;
using Modules.Pricing.Infrastructure.Persistence;

namespace Modules.Pricing.Infrastructure;

public static class PricingDatabaseInitializer
{
    public static async Task InitializePricingDatabaseAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PricingDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);

        var seeder = scope.ServiceProvider.GetRequiredService<DefaultPriceListsSeeder>();
        await seeder.SeedAsync(cancellationToken);
    }
}
