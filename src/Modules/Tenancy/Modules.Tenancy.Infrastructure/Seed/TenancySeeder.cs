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

    public static Task<Guid> SeedTenantWithOwnerAsync(
        this IServiceProvider services,
        Guid ownerUserId,
        CancellationToken cancellationToken = default) =>
        services.SeedTenantWithOwnerAsync(
            SeedTenantId,
            SeedTenantSlug,
            SeedTenantDisplayName,
            ownerUserId,
            Membership.RegistrationOrigin,
            cancellationToken);

    /// <summary>
    /// Crea el tenant **y su membresía dueña juntos**, por el dominio y en una sola escritura, y
    /// devuelve el id de esa membresía: es el <c>MemberId</c> al que apuntan <c>advisor_id</c>,
    /// <c>created_by</c> y <c>converted_by</c>.
    ///
    /// Los dos nacen juntos porque no hay tenant sin owner: <c>Tenant.Create</c> lo exige. Antes
    /// esto eran dos pasos —sembrar el tenant, y después nombrarle owner— y entre uno y otro
    /// quedaba una fila persistida sin autoridad, que es la que dejaba a la membresía dueña sin la
    /// protección del agregado.
    ///
    /// No hace falta que la membresía exista para que el tenant la nombre, ni al revés: los dos
    /// ids se acuñan en memoria antes de persistir.
    ///
    /// La usan la semilla de arranque y la carga de exportación (spec 2026-09-13), cada una con su
    /// id y su origen — el origen es la partida de nacimiento de la membresía, no autoridad, así
    /// que cada quien conserva el suyo.
    /// </summary>
    /// <remarks>
    /// Idempotente como el resto del sembrador: si el tenant ya existe devuelve el id de su owner
    /// sin tocar nada, así que correrlo dos veces no falla.
    /// </remarks>
    public static async Task<Guid> SeedTenantWithOwnerAsync(
        this IServiceProvider services,
        Guid tenantId,
        string slug,
        string displayName,
        Guid ownerUserId,
        string origin,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();

        var id = new TenantId(tenantId);
        var existing = await dbContext.Tenants.SingleOrDefaultAsync(
            tenant => tenant.Id == id, cancellationToken);
        if (existing is not null)
        {
            return existing.OwnerMembershipId.Value;
        }

        var now = DateTimeOffset.UtcNow;
        var ownerMembershipId = MembershipId.New();

        dbContext.Tenants.Add(Tenant.Create(
            id,
            slug,
            displayName,
            "es-CO",
            "America/Bogota",
            "yyyy-MM-dd",
            ownerMembershipId,
            now));
        dbContext.Memberships.Add(Membership.CreateActive(
            ownerMembershipId,
            ownerUserId,
            id,
            ["admin"],
            origin,
            now));

        await dbContext.SaveChangesAsync(cancellationToken);
        return ownerMembershipId.Value;
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
