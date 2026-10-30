using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.ServiceContracts.Infrastructure.Persistence;

namespace Modules.ServiceContracts.Infrastructure;

public static class ServiceContractsDatabaseInitializer
{
    public static async Task InitializeServiceContractsDatabaseAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ServiceContractsDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
    }
}
