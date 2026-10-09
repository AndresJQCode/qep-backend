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

    /// <summary>
    /// El tenant operador (QCode, spec 2026-10-08). Constante por la misma razón que
    /// <see cref="SeedTenantId"/>, y además porque es el valor por defecto de
    /// <c>Platform:OperatorTenantId</c> en <c>appsettings.json</c>: si se mueve uno, se mueve el otro.
    /// `...0006` no choca con la semilla (`...0003`) ni con la carga de exportación (`...0004` y
    /// `...0005`).
    /// </summary>
    public static readonly Guid OperatorTenantId =
        Guid.Parse("01900000-0000-7000-8000-000000000006");

    public const string OperatorTenantSlug = "qcode";
    public const string OperatorTenantDisplayName = "QCode";

    /// <summary>
    /// Qué encuentra la semilla del tenant operador antes de crear nada. Se mira primero para no
    /// crear el usuario dueño de un tenant que no se va a poder crear.
    /// </summary>
    public enum OperatorTenantSeedState
    {
        /// <summary>Ni el id ni el slug existen: se puede sembrar.</summary>
        Missing,

        /// <summary>El tenant ya está: la semilla no toca nada.</summary>
        AlreadySeeded,

        /// <summary>
        /// Otro tenant —por ejemplo QCode registrado por el signup— ya tiene el slug. Crearlo
        /// reventaría contra <c>IX_tenants_slug</c> y tumbaría el arranque de cada pod.
        /// </summary>
        SlugTaken,
    }

    public static async Task<(OperatorTenantSeedState State, Guid? SlugHolderId)> InspectOperatorTenantAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();

        var id = new TenantId(OperatorTenantId);
        var found = await dbContext.Tenants
            .AsNoTracking()
            .Where(tenant => tenant.Id == id || tenant.Slug == OperatorTenantSlug)
            .Select(tenant => tenant.Id)
            .ToListAsync(cancellationToken);

        if (found.Contains(id))
        {
            return (OperatorTenantSeedState.AlreadySeeded, null);
        }

        return found.Count == 0
            ? (OperatorTenantSeedState.Missing, null)
            : (OperatorTenantSeedState.SlugTaken, found[0].Value);
    }

    /// <summary>
    /// true si el usuario tiene una membresía —en cualquier estado— en un tenant que no es el
    /// operador. La semilla se niega a hacer operador a alguien que ya pertenece a otro tenant: un
    /// email reutilizado o mal tipeado le daría poder sobre toda la plataforma sin que se note. El
    /// tenant operador se excluye para que el pod que pierde una carrera de arranque no confunda la
    /// membresía que acaba de crear el otro con una ajena.
    /// </summary>
    public static async Task<bool> HasMembershipOutsideOperatorTenantAsync(
        this IServiceProvider services,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();

        var operatorTenant = new TenantId(OperatorTenantId);
        return await dbContext.Memberships.AnyAsync(
            membership => membership.UserId == userId && membership.TenantId != operatorTenant,
            cancellationToken);
    }

    /// <summary>
    /// Siembra QCode con los siete módulos y su dueño admin, por el mismo camino que Origen
    /// botánico. Idempotente por id, como el resto.
    /// </summary>
    public static Task<Guid> SeedOperatorTenantWithOwnerAsync(
        this IServiceProvider services,
        Guid ownerUserId,
        CancellationToken cancellationToken = default) =>
        services.SeedTenantWithOwnerAsync(
            OperatorTenantId,
            OperatorTenantSlug,
            OperatorTenantDisplayName,
            ownerUserId,
            Membership.RegistrationOrigin,
            cancellationToken);

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

        // Spec 2026-10-07, «Semilla»: todos, pos y messaging incluidos, en la misma escritura que el tenant.
        // Sólo al crear: si el tenant ya existía el método devolvió arriba, y una base local vieja
        // prende pos con el SQL de «Operación» (README § Módulos por tenant).
        foreach (var key in TenantModuleKeys.All)
        {
            dbContext.TenantModules.Add(TenantModule.Create(id, key, TenantModuleSources.Seed, now, note: null));
        }

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
