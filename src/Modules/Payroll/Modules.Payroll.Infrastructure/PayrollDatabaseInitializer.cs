using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Payroll.Infrastructure.Persistence;

namespace Modules.Payroll.Infrastructure;

public static class PayrollDatabaseInitializer
{
    public static async Task InitializePayrollDatabaseAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PayrollDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
    }
}
