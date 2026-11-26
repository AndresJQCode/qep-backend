using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Scheduling.Infrastructure.Persistence;

namespace Modules.Scheduling.Infrastructure;

public static class SchedulingDatabaseInitializer
{
    public static async Task InitializeSchedulingDatabaseAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SchedulingDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
    }
}
