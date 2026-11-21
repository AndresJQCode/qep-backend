using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Hr.Infrastructure.Persistence;

namespace Modules.Hr.Infrastructure;

public static class HrDatabaseInitializer
{
    public static async Task InitializeHrDatabaseAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<HrDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
    }
}
