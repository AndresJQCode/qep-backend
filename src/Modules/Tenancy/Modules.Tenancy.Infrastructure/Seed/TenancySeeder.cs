using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;

namespace Modules.Tenancy.Infrastructure.Seed;

/// <summary>
/// La mitad de Tenancy de la semilla de arranque. Siembra sólo tablas de este módulo.
/// </summary>
public static class TenancySeeder
{
    /// <summary>
    /// Constante y no configuración: es lo que hace que el id sobreviva a borrar la base, y que
    /// Catalog no tenga que preguntarle a Tenancy quién es el tenant. Se elige `...0003` para no
    /// chocar con DevelopmentTenantId (`...0001`) ni con el sujeto de desarrollo (`...0002`).
    /// </summary>
    public static readonly Guid SeedTenantId =
        Guid.Parse("01900000-0000-7000-8000-000000000003");

    public const string SeedTenantSlug = "origen-botanico";
    public const string SeedTenantDisplayName = "Origen botánico";

    public static async Task SeedTenantAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();

        var tenantId = new TenantId(SeedTenantId);
        if (await dbContext.Tenants.AnyAsync(
            tenant => tenant.Id == tenantId, cancellationToken))
        {
            return;
        }

        dbContext.Tenants.Add(Tenant.Create(
            tenantId,
            SeedTenantSlug,
            SeedTenantDisplayName,
            "es-CO",
            "America/Bogota",
            "yyyy-MM-dd",
            DateTimeOffset.UtcNow));
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
