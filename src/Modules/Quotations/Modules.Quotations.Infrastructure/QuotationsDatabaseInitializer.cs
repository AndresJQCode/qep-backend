using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Infrastructure.Persistence;

namespace Modules.Quotations.Infrastructure;

public static class QuotationsDatabaseInitializer
{
    public static async Task InitializeQuotationsDatabaseAsync(
        this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
    }
}
