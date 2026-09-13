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

    public static Task SeedTenantAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default) =>
        services.SeedTenantAsync(SeedTenantId, SeedTenantSlug, SeedTenantDisplayName, cancellationToken);

    /// <summary>
    /// Crea el tenant por id si no existe, por el dominio: rigen las mismas reglas de slug que en un
    /// registro. La usan la semilla de arranque y la carga de exportación (spec 2026-09-13), cada una
    /// con su id.
    /// </summary>
    public static async Task SeedTenantAsync(
        this IServiceProvider services,
        Guid tenantId,
        string slug,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();

        var id = new TenantId(tenantId);
        if (await dbContext.Tenants.AnyAsync(tenant => tenant.Id == id, cancellationToken))
        {
            return;
        }

        dbContext.Tenants.Add(Tenant.Create(
            id,
            slug,
            displayName,
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
        await services.SeedAdminMembershipAsync(
            SeedTenantId, ownerUserId, Membership.RegistrationOrigin, cancellationToken);
    }

    /// <summary>
    /// Crea una membresía admin ya en <c>Active</c> si ese usuario no tiene una en ese tenant, y
    /// devuelve su id: es el <c>MemberId</c> al que apuntan advisor_id, created_by y converted_by.
    /// </summary>
    public static async Task<Guid> SeedAdminMembershipAsync(
        this IServiceProvider services,
        Guid tenantId,
        Guid userId,
        string origin,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();

        var tenant = new TenantId(tenantId);
        var existing = await dbContext.Memberships.SingleOrDefaultAsync(
            membership => membership.TenantId == tenant && membership.UserId == userId,
            cancellationToken);
        if (existing is not null)
        {
            return existing.Id.Value;
        }

        var created = Membership.CreateActive(
            MembershipId.New(),
            userId,
            tenant,
            ["admin"],
            origin,
            DateTimeOffset.UtcNow);
        dbContext.Memberships.Add(created);
        await dbContext.SaveChangesAsync(cancellationToken);
        return created.Id.Value;
    }
}
