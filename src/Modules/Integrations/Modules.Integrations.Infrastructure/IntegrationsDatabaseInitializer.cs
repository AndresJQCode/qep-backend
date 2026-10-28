using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Integrations.Infrastructure.Persistence;

namespace Modules.Integrations.Infrastructure;

public static class IntegrationsDatabaseInitializer
{
    public static async Task InitializeIntegrationsDatabaseAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<IntegrationsDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
    }
}
