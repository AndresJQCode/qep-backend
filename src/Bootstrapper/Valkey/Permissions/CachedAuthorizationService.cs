using System.Text.Json;
using System.Text.Json.Serialization;
using Modules.Authorization.Application;
using StackExchange.Redis;

namespace Bootstrapper.Valkey.Permissions;

/// <summary>
/// La caché de permisos con <c>Valkey:Areas:Permissions</c> prendido (spec 2026-11-13, D-5; plan P5). Decora a
/// <see cref="AuthorizationService"/>: <see cref="ResolvePermissionsAsync"/> lee en un solo <c>MGET</c> la entrada
/// <c>qep:perm:{userId}:{tenantId}</c> y la versión de acceso del tenant <c>qep:tenantver:{tenantId}</c>. La entrada sirve sólo si
/// se construyó con esa misma versión; si no, resuelve en Postgres y guarda el resultado —también un <c>null</c>, «sin membresía
/// activa»— con la versión que leyó <b>antes</b> de resolver, <c>EX 60</c>: un cambio que suba la versión en el medio deja la
/// entrada nacida vieja. Si el <c>MGET</c> no responde, resuelve en Postgres y no cachea: sin versión no hay con qué invalidar
/// después. Nunca concede por un fallo de Valkey.
/// </summary>
/// <remarks>
/// Una versión ausente no vale 0: con <c>allkeys-lru</c> Valkey puede desalojar <c>qep:tenantver</c> y dejar viva una entrada
/// construida con una versión que el contador, si volviera a empezar, alcanzaría otra vez (ABA). Ausente, la entrada no se sirve:
/// se siembra la versión con <see cref="TenantAccessVersionBase"/> (<c>SET NX</c>, 30 días) y sólo se cachea si la siembra fue de
/// este request, porque es la única forma de saber el valor sin otro viaje. <see cref="ValkeyTenantAccessVersion"/> siembra igual
/// antes de su <c>INCR</c>.
/// </remarks>
internal sealed class CachedAuthorizationService(AuthorizationService inner, ValkeyGateway gateway, TimeProvider time)
    : IAuthorizationService
{
    /// <summary>Lo que vive una entrada: el techo de un cambio que no pudo subir la versión (D-5, «Respaldo»).</summary>
    internal static readonly TimeSpan EntryTtl = TimeSpan.FromSeconds(60);

    // El mismo cuerpo que AuthorizationService.AuthorizeAsync, sobre la resolución cacheada.
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
        var entryKey = PermissionKeys.Entry(subjectId, tenantId);
        var versionKey = PermissionKeys.TenantVersion(tenantId);
        var read = await gateway.TryAsync(
            ValkeyArea.Permissions,
            database => database.StringGetAsync([entryKey, versionKey]),
            cancellationToken).ConfigureAwait(false);
        if (read is not { Succeeded: true, Value: [var entry, var storedVersion] })
        {
            return await inner.ResolvePermissionsAsync(subjectId, tenantId, cancellationToken).ConfigureAwait(false);
        }

        long version;
        if (storedVersion.IsNull)
        {
            // Ausente (nunca existió o se desalojó): nada de lo cacheado sirve. Se siembra antes de resolver, y se cachea con la
            // semilla sólo si la escribió este request.
            var seed = TenantAccessVersionBase.Seed(time);
            var seeded = await gateway.TryAsync(
                ValkeyArea.Permissions,
                database => database.StringSetAsync(
                    versionKey, seed, TenantAccessVersionBase.Ttl, When.NotExists),
                cancellationToken).ConfigureAwait(false);
            if (seeded is not { Succeeded: true, Value: true })
            {
                return await inner.ResolvePermissionsAsync(subjectId, tenantId, cancellationToken).ConfigureAwait(false);
            }

            version = seed;
        }
        else if (!storedVersion.TryParse(out version))
        {
            // Un valor que no es un entero (nadie lo escribe así) no es una versión: se resuelve sin cachear.
            return await inner.ResolvePermissionsAsync(subjectId, tenantId, cancellationToken).ConfigureAwait(false);
        }
        else if (!entry.IsNull && TryReadEntry(entry) is { } cached && cached.Version == version)
        {
            return cached.Permissions;
        }

        var resolved = await inner.ResolvePermissionsAsync(subjectId, tenantId, cancellationToken).ConfigureAwait(false);
        var value = JsonSerializer.Serialize(new PermissionEntry(version, resolved?.ToArray()));
        await gateway.TryAsync(
            ValkeyArea.Permissions,
            database => database.StringSetAsync(entryKey, value, EntryTtl),
            cancellationToken).ConfigureAwait(false);
        return resolved;
    }

    private static PermissionEntry? TryReadEntry(RedisValue entry)
    {
        try
        {
            return JsonSerializer.Deserialize<PermissionEntry>((string)entry!);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>El valor de la entrada: <c>{"v":&lt;versión&gt;,"p":[…]|null}</c>.</summary>
    private sealed record PermissionEntry(
        [property: JsonPropertyName("v")] long Version,
        [property: JsonPropertyName("p")] string[]? Permissions);
}

/// <summary>
/// La base de una versión de acceso nueva: milisegundos Unix. Una versión sembrada después de un desalojo arranca por encima de
/// cualquier versión anterior del tenant (que partió de una base más vieja y sumó menos bumps que milisegundos pasaron), así que
/// una entrada sobreviviente nunca vuelve a coincidir. Depende de que los relojes de los pods no retrocedan más que eso.
/// </summary>
internal static class TenantAccessVersionBase
{
    /// <summary>Lo que vive el contador sin cambios; cada bump lo renueva.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromDays(30);

    public static long Seed(TimeProvider time) => time.GetUtcNow().ToUnixTimeMilliseconds();
}

/// <summary>Las claves de la caché de permisos (D-5): GUID en formato <c>D</c>, en minúsculas.</summary>
internal static class PermissionKeys
{
    public static RedisKey Entry(Guid userId, Guid tenantId) => $"{ValkeyKeys.Prefix}perm:{userId:D}:{tenantId:D}";

    public static RedisKey TenantVersion(Guid tenantId) => $"{ValkeyKeys.Prefix}tenantver:{tenantId:D}";
}
