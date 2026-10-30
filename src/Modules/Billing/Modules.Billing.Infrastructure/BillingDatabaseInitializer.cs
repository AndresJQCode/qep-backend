using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Billing.Infrastructure.Persistence;

namespace Modules.Billing.Infrastructure;

public static class BillingDatabaseInitializer
{
    public static async Task InitializeBillingDatabaseAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
    }
}
