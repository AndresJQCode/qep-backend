using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using Modules.Authorization.Application;
using Modules.Tenancy.Application;

namespace Bootstrapper.Valkey.Permissions;

/// <summary>
/// La caché de permisos del pod, la que rige con <c>Valkey:Areas:Permissions</c> apagado. Antes el área apagada era «cada
/// request resuelve en Postgres»: con la base en otro nodo (10-20 ms de ida y vuelta por consulta) eso eran cuatro viajes en serie
/// —membresía, estado del tenant, roles del tenant y módulos— antes de que corra ningún handler. Es el mismo diseño que
/// <see cref="CachedAuthorizationService"/> (spec 2026-11-13, D-5) con la versión de acceso del tenant en un contador del proceso:
/// la entrada <c>(usuario, tenant)</c> guarda la versión con la que se construyó y sirve sólo mientras coincida con la actual, y
/// vive a lo sumo <see cref="EntryTtl"/>. Todo caso de uso que cambia lo que alguien puede hacer en el tenant sube la versión
/// (<see cref="ITenantAccessVersion"/>) antes y después del guardado, así que la caché no sobrevive a aceptar, suspender, quitar o
/// cambiar los roles de una membresía, ni a un cambio de módulos o de estado del tenant.
/// </summary>
/// <remarks>
/// Con más de una réplica el contador de un pod no invalida al otro: por eso el área de Valkey es prerrequisito de
/// <c>replicas &gt; 1</c> (<c>MultiReplicaInventory.SharedStateSwitches</c>), y hasta entonces ésta es la copia local.
/// </remarks>
internal sealed class MemoryPermissionCache(TimeProvider time) : IDisposable
{
    /// <summary>Lo que vive una entrada: el mismo techo que la de Valkey.</summary>
    internal static readonly TimeSpan EntryTtl = CachedAuthorizationService.EntryTtl;

    // Tope por tamaño (una unidad por entrada): un tenant con mil miembros son mil entradas, y nada crece sin límite aunque
    // alguien pruebe claves al azar.
    private readonly MemoryCache _entries = new(new MemoryCacheOptions { SizeLimit = 50_000 });
    private readonly ConcurrentDictionary<Guid, long> _versions = new();

    public long CurrentVersion(Guid tenantId) => _versions.GetOrAdd(tenantId, 0L);

    public void Bump(Guid tenantId) => _versions.AddOrUpdate(tenantId, 1L, (_, current) => current + 1);

    public bool TryGet(Guid userId, Guid tenantId, out IReadOnlyCollection<string>? permissions)
    {
        permissions = null;
        if (!_entries.TryGetValue((userId, tenantId), out Entry? entry) || entry is null)
        {
            return false;
        }

        if (entry.Version != CurrentVersion(tenantId) || time.GetUtcNow() >= entry.ExpiresAt)
        {
            _entries.Remove((userId, tenantId));
            return false;
        }

        permissions = entry.Permissions;
        return true;
    }

    /// <param name="version">La versión leída <b>antes</b> de resolver: un bump en el medio deja la entrada nacida vieja.</param>
    public void Set(Guid userId, Guid tenantId, long version, IReadOnlyCollection<string>? permissions) =>
        _entries.Set(
            (userId, tenantId),
            new Entry(version, permissions?.ToArray(), time.GetUtcNow() + EntryTtl),
            new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = EntryTtl });

    public void Dispose() => _entries.Dispose();

    private sealed record Entry(long Version, string[]? Permissions, DateTimeOffset ExpiresAt);
}

/// <summary>
/// <see cref="IAuthorizationService"/> con el área de Valkey apagada: la caché del pod delante de <see cref="AuthorizationService"/>.
/// El cuerpo de <see cref="AuthorizeAsync"/> es el mismo del servicio, sobre la resolución cacheada.
/// </summary>
internal sealed class MemoryCachedAuthorizationService(AuthorizationService inner, MemoryPermissionCache cache)
    : IAuthorizationService
{
    public async Task<AuthorizationDecision> AuthorizeAsync(
        Guid subjectId,
        Guid tenantId,
        string permission,
        CancellationToken cancellationToken)
    {
        var permissions = await ResolvePermissionsAsync(subjectId, tenantId, cancellationToken).ConfigureAwait(false);
        if (permissions is null)
        {
            return AuthorizationDecision.Deny("no_active_membership");
        }

        return permissions.Contains(permission, StringComparer.Ordinal)
            ? AuthorizationDecision.Allow()
            : AuthorizationDecision.Deny("permission_denied");
    }

    public async Task<IReadOnlyCollection<string>?> ResolvePermissionsAsync(
        Guid subjectId,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        if (cache.TryGet(subjectId, tenantId, out var cached))
        {
            return cached;
        }

        var version = cache.CurrentVersion(tenantId);
        var resolved = await inner.ResolvePermissionsAsync(subjectId, tenantId, cancellationToken).ConfigureAwait(false);
        cache.Set(subjectId, tenantId, version, resolved);
        return resolved;
    }
}

/// <summary>La versión de acceso con el área de Valkey apagada: el contador de <see cref="MemoryPermissionCache"/>.</summary>
internal sealed class MemoryTenantAccessVersion(MemoryPermissionCache cache) : ITenantAccessVersion
{
    public Task BumpAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        cache.Bump(tenantId);
        return Task.CompletedTask;
    }
}
