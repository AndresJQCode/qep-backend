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

    /// <summary>
    /// Crea la membresía del owner, ya en <c>Active</c>. Usa
    /// <see cref="Membership.RegistrationOrigin"/> y no un origen propio porque esta membresía
    /// **es** la del owner del tenant, el mismo caso que <c>TenantRegistrationService</c>: así
    /// hereda la protección del agregado, que impide suspenderla, quitarla o dejarla sin el rol
    /// admin. La contrapartida es que tampoco se puede quitar por la API — correcto para un
    /// tenant cuya única salida es borrar la base y volver a sembrarlo.
    /// </summary>
    public static async Task SeedOwnerMembershipAsync(
        this IServiceProvider services,
        Guid ownerUserId,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();

        var tenantId = new TenantId(SeedTenantId);
        if (await dbContext.Memberships.AnyAsync(
            membership => membership.TenantId == tenantId && membership.UserId == ownerUserId,
            cancellationToken))
        {
            return;
        }

        dbContext.Memberships.Add(Membership.CreateActive(
            MembershipId.New(),
            ownerUserId,
            tenantId,
            ["admin"],
            Membership.RegistrationOrigin,
            DateTimeOffset.UtcNow));
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
