using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Wholesale.Infrastructure.Persistence;

namespace Modules.Wholesale.Infrastructure;

public static class WholesaleDatabaseInitializer
{
    public static async Task InitializeWholesaleDatabaseAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WholesaleDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
    }
}
