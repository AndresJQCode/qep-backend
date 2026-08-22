using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Audit.Infrastructure.Persistence;

namespace Modules.Audit.Infrastructure;

public static class AuditDatabaseInitializer
{
    public static async Task InitializeAuditDatabaseAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
    }
}
