using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Loans.Infrastructure.Persistence;

namespace Modules.Loans.Infrastructure;

public static class LoansDatabaseInitializer
{
    public static async Task InitializeLoansDatabaseAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LoansDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
    }
}
