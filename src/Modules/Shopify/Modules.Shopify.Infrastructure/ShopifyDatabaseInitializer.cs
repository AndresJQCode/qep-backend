using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Shopify.Infrastructure.Persistence;

namespace Modules.Shopify.Infrastructure;

public static class ShopifyDatabaseInitializer
{
    public static async Task InitializeShopifyDatabaseAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ShopifyDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
    }
}
