using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Companies.Infrastructure.Persistence;

namespace Modules.Companies.Infrastructure;

public static class CompaniesDatabaseInitializer
{
    public static async Task InitializeCompaniesDatabaseAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CompaniesDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
    }
}
