using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Authorization.Infrastructure.Persistence;

namespace Modules.Authorization.Infrastructure;

public static class AuthorizationDatabaseInitializer
{
    public static async Task InitializeAuthorizationDatabaseAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
    }
}
