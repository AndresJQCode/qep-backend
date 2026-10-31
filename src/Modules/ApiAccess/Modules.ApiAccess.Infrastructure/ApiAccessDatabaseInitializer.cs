using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.ApiAccess.Infrastructure.Persistence;

namespace Modules.ApiAccess.Infrastructure;

public static class ApiAccessDatabaseInitializer
{
    public static async Task InitializeApiAccessDatabaseAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApiAccessDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
    }
}
