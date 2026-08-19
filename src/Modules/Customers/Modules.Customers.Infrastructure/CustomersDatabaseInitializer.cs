using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Customers.Infrastructure.Persistence;

namespace Modules.Customers.Infrastructure;

public static class CustomersDatabaseInitializer
{
    public static async Task InitializeCustomersDatabaseAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CustomersDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
    }
}
