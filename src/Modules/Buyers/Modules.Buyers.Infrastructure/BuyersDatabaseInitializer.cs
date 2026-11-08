using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Buyers.Infrastructure.Persistence;

namespace Modules.Buyers.Infrastructure;

public static class BuyersDatabaseInitializer
{
    public static async Task InitializeBuyersDatabaseAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<BuyersDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
    }
}
