using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Purchases.Infrastructure.Persistence;

namespace Modules.Purchases.Infrastructure;

public static class PurchasesDatabaseInitializer
{
    public static async Task InitializePurchasesDatabaseAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PurchasesDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
    }
}
