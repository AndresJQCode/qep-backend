using Modules.Authorization.Domain;

namespace Modules.Authorization.Application;

/// <summary>
/// Combina el catalogo del codigo con los roles que definio el tenant.
/// </summary>
/// <remarks>
/// Scoped, no singleton: un rol custom cambia sin reiniciar el proceso. Y memoiza por scope
/// —o sea, por request— porque `ExternalClaimsTransformation` resuelve permisos en cada uno,
/// y una pantalla que pregunta tres veces serian tres consultas por request.
///
/// La memoizacion es por tenant y no una sola: un request atiende a un tenant, pero nada lo
/// garantiza, y cachear la respuesta de uno para otro seria servir los roles de una
/// organizacion dentro de otra.
/// </remarks>
public sealed class TenantRoleCatalog(
    IRoleCatalog systemCatalog,
    ICustomRoleReader customRoleReader) : ITenantRoleCatalog
{
    private readonly Dictionary<Guid, IReadOnlyDictionary<string, TenantRoleDefinition>> _byTenant =
        [];

    public async Task<IReadOnlyCollection<string>> PermissionsForAsync(
        Guid tenantId,
        IReadOnlyCollection<string> roles,
        CancellationToken cancellationToken)
    {
        var catalog = await ResolveAsync(tenantId, cancellationToken);

        return roles
            // Un rol que no esta en el catalogo no concede nada, y eso no es un error: puede
            // ser uno retirado con membresias que todavia lo nombran. Tirar aca dejaria a esa
            // gente con un 500 en cada request en vez de con menos permisos.
            .SelectMany(role => catalog.TryGetValue(role, out var definition)
                ? definition.Permissions
                : [])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<IReadOnlyCollection<TenantRoleDefinition>> ListRolesAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var catalog = await ResolveAsync(tenantId, cancellationToken);
        return catalog.Values
            .OrderBy(definition => definition.IsSystem ? 0 : 1)
            .ThenBy(definition => definition.Role, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<bool> ContainsRoleAsync(
        Guid tenantId,
        string role,
        CancellationToken cancellationToken)
    {
        var catalog = await ResolveAsync(tenantId, cancellationToken);
        return catalog.ContainsKey(role);
    }

    private async Task<IReadOnlyDictionary<string, TenantRoleDefinition>> ResolveAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        if (_byTenant.TryGetValue(tenantId, out var cached))
        {
            return cached;
        }

        var catalog = new Dictionary<string, TenantRoleDefinition>(StringComparer.Ordinal);

        foreach (var definition in systemCatalog.ListRoles())
        {
            catalog[definition.Role] = new TenantRoleDefinition(
                definition.Role,
                definition.DisplayName,
                definition.Description,
                definition.Category,
                definition.RiskLevel,
                definition.Permissions,
                IsSystem: true,
                // Los de sistema no versionan: cambian con un deploy, no con un PATCH, asi que
                // no hay concurrencia optimista que sostener sobre ellos.
                Version: 0);
        }

        var custom = await customRoleReader.ListAsync(tenantId, cancellationToken);
        foreach (var role in custom)
        {
            // `Role.Create` ya rechaza una clave reservada, asi que esto no deberia pisar un
            // rol de sistema. Se indexa igual por clave —y no se suma a ciegas— para que si
            // una fila vieja se colara, el catalogo siga teniendo una definicion por clave.
            catalog[role.Key] = new TenantRoleDefinition(
                role.Key,
                role.DisplayName,
                role.Description,
                "Custom",
                // Un rol custom hereda el riesgo del permiso mas alto que concede: decir
                // "medium" sobre un rol que da `advisorship.manage` seria mentirle a quien lo
                // asigna.
                HighestRiskOf(role.Permissions),
                role.Permissions,
                IsSystem: false,
                role.Version);
        }

        _byTenant[tenantId] = catalog;
        return catalog;
    }

    private string HighestRiskOf(IReadOnlyCollection<string> permissions)
    {
        var known = systemCatalog.ListPermissions()
            .Where(permission => permissions.Contains(permission.Permission, StringComparer.Ordinal))
            .Select(permission => permission.RiskLevel)
            .ToArray();

        if (known.Contains("high", StringComparer.Ordinal)) return "high";
        if (known.Contains("medium", StringComparer.Ordinal)) return "medium";
        return "low";
    }
}
