using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;

namespace Modules.Tenancy.Infrastructure;

public static class TenancyDatabaseInitializer
{
    public static readonly Guid DevelopmentTenantId =
        Guid.Parse("01900000-0000-7000-8000-000000000001");

    public static async Task InitializeTenancyDatabaseAsync(
        this IServiceProvider services,
        IHostEnvironment environment,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);

        if (!environment.IsDevelopment() ||
            await dbContext.Tenants.AnyAsync(cancellationToken))
        {
            return;
        }

        dbContext.Tenants.Add(Tenant.Create(
            new TenantId(DevelopmentTenantId),
            "qcode-demo",
            "QCode Demo",
            "es-CO",
            "America/Bogota",
            "yyyy-MM-dd",
            DateTimeOffset.UtcNow));
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
