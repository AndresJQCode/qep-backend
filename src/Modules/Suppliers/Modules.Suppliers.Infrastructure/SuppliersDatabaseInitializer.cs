using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Suppliers.Infrastructure.Persistence;

namespace Modules.Suppliers.Infrastructure;

public static class SuppliersDatabaseInitializer
{
    public static async Task InitializeSuppliersDatabaseAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SuppliersDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
    }
}
