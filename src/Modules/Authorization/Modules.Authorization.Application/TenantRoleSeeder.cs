using BuildingBlocks.Application;
using Modules.Authorization.Domain;

namespace Modules.Authorization.Application;

/// <summary>
/// Guarda los roles sembrados y la marca de cada tenant ya sembrado. Lo implementa Infrastructure.
/// </summary>
public interface ITenantRoleSeedStore
{
    Task<bool> IsSeededAsync(Guid tenantId, CancellationToken cancellationToken);

    Task<IReadOnlySet<Guid>> SeededTenantIdsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyCollection<string>> RoleKeysAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Guarda los roles y la marca juntos. <c>false</c> significa que otra siembra del tenant ganó la carrera.</summary>
    Task<bool> TrySaveAsync(
        Guid tenantId,
        IReadOnlyCollection<Role> roles,
        DateTimeOffset seededAt,
        CancellationToken cancellationToken);
}

/// <summary>
/// Los tenants que hay que revisar al arrancar. Lo implementa Bootstrapper: Authorization no lee las tablas de Tenancy.
/// </summary>
public interface IRoleSeedTenantSource
{
    /// <summary>Todos los tenants, en cualquier estado.</summary>
    Task<IReadOnlyCollection<Guid>> ListTenantIdsAsync(CancellationToken cancellationToken);
}

public interface ITenantRoleSeeder
{
    /// <summary>Siembra el tenant si no tiene la marca. <c>true</c> si esta llamada lo sembró.</summary>
    Task<bool> SeedAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Siembra todo tenant sin marca y devuelve cuántos sembró. Una falla sube.</summary>
    Task<int> SeedMissingAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Crea en un tenant los roles de las plantillas, una sola vez (spec 2026-11-25, roles por tenant).
/// </summary>
/// <remarks>
/// La marca es lo que impide revivir un rol borrado o pisar uno editado: con marca no se hace nada. Sin marca se crea
/// cada plantilla cuya clave el tenant no tenga, y la marca se escribe aunque no falte ninguna.
///
/// No sube la versión de acceso del tenant: cada clave resuelve los mismos permisos que antes de la siembra, así que la
/// caché de permisos no sirve nada distinto.
/// </remarks>
public sealed class TenantRoleSeeder(
    IEnumerable<RoleTemplate> templates,
    ITenantRoleSeedStore store,
    IRoleSeedTenantSource tenants,
    IClock clock) : ITenantRoleSeeder
{
    public async Task<bool> SeedAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (await store.IsSeededAsync(tenantId, cancellationToken))
        {
            return false;
        }

        var existing = (await store.RoleKeysAsync(tenantId, cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
        var now = clock.UtcNow;
        var roles = templates
            .Where(template => !existing.Contains(template.Key))
            .Select(template => Role.Create(
                RoleId.New(),
                tenantId,
                template.Key,
                template.DisplayName,
                template.Description,
                template.Permissions,
                now))
            .ToArray();

        return await store.TrySaveAsync(tenantId, roles, now, cancellationToken);
    }

    public async Task<int> SeedMissingAsync(CancellationToken cancellationToken)
    {
        var seeded = await store.SeededTenantIdsAsync(cancellationToken);
        var count = 0;
        // En serie: el store comparte un DbContext, que no admite dos operaciones a la vez.
        foreach (var tenantId in await tenants.ListTenantIdsAsync(cancellationToken))
        {
            if (!seeded.Contains(tenantId) && await SeedAsync(tenantId, cancellationToken))
            {
                count++;
            }
        }

        return count;
    }
}
