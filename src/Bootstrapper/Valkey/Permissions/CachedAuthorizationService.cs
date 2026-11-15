using System.Text.Json;
using System.Text.Json.Serialization;
using Modules.Authorization.Application;
using StackExchange.Redis;

namespace Bootstrapper.Valkey.Permissions;

/// <summary>
/// La caché de permisos con <c>Valkey:Areas:Permissions</c> prendido (spec 2026-11-13, D-5; plan P5). Decora a
/// <see cref="AuthorizationService"/>: <see cref="ResolvePermissionsAsync"/> lee en un solo <c>MGET</c> la entrada
/// <c>qep:perm:{userId}:{tenantId}</c> y la versión de acceso del tenant <c>qep:tenantver:{tenantId}</c> (ausente = 0). La entrada
/// sirve sólo si se construyó con esa misma versión; si no, resuelve en Postgres y guarda el resultado —también un <c>null</c>,
/// «sin membresía activa»— con la versión que leyó <b>antes</b> de resolver, <c>EX 60</c>: un cambio que suba la versión en el
/// medio deja la entrada nacida vieja. Si el <c>MGET</c> no responde, resuelve en Postgres y no cachea: sin versión no hay con qué
/// invalidar después. Nunca concede por un fallo de Valkey.
/// </summary>
internal sealed class CachedAuthorizationService(AuthorizationService inner, ValkeyGateway gateway) : IAuthorizationService
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
        if (read is not { Succeeded: true, Value: [var entry, var storedVersion] }
            || !TryReadVersion(storedVersion, out var version))
        {
            return await inner.ResolvePermissionsAsync(subjectId, tenantId, cancellationToken).ConfigureAwait(false);
        }

        if (!entry.IsNull && TryReadEntry(entry) is { } cached && cached.Version == version)
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

    // Un valor que no es un entero (nadie lo escribe así) no es una versión: se resuelve sin cachear.
    private static bool TryReadVersion(RedisValue stored, out long version)
    {
        if (stored.IsNull)
        {
            version = 0;
            return true;
        }

        return stored.TryParse(out version);
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

/// <summary>Las claves de la caché de permisos (D-5): GUID en formato <c>D</c>, en minúsculas.</summary>
internal static class PermissionKeys
{
    public static RedisKey Entry(Guid userId, Guid tenantId) => $"{ValkeyKeys.Prefix}perm:{userId:D}:{tenantId:D}";

    public static RedisKey TenantVersion(Guid tenantId) => $"{ValkeyKeys.Prefix}tenantver:{tenantId:D}";
}
