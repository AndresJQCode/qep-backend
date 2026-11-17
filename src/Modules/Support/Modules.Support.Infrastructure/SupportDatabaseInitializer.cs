using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Support.Infrastructure.Persistence;

namespace Modules.Support.Infrastructure;

public static class SupportDatabaseInitializer
{
    public static async Task InitializeSupportDatabaseAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SupportDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
    }
}
