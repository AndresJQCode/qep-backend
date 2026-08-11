using System.Security.Cryptography;
using System.Text;

namespace Modules.Authorization.Application;

/// <summary>
/// Un rol acotado al tenant y los permisos que concede. Las definiciones de rol de sistema
/// se versionan con el código y son propiedad del módulo que protege el caso de uso; se
/// registran en tiempo de composición. Los roles custom (en base) son un agregado posterior.
/// </summary>
public sealed record RoleDefinition(
    string Role,
    string DisplayName,
    string Description,
    string Category,
    string RiskLevel,
    IReadOnlyCollection<string> Permissions);

public sealed record PermissionDefinition(
    string Permission,
    string DisplayName,
    string Description,
    string Category,
    string RiskLevel);

/// <summary>Resuelve una referencia de rol de una membresía a los permisos que concede.</summary>
public interface IRoleCatalog
{
    IReadOnlyCollection<string> PermissionsFor(string role);

    IReadOnlyCollection<RoleDefinition> ListRoles();

    IReadOnlyCollection<PermissionDefinition> ListPermissions();

    bool ContainsRole(string role);

    string CatalogVersion { get; }
}

public sealed class RoleCatalog : IRoleCatalog
{
    private readonly Dictionary<string, IReadOnlyCollection<string>> _roles;
    private readonly RoleDefinition[] _roleDefinitions;
    private readonly PermissionDefinition[] _permissionDefinitions;

    public string CatalogVersion { get; }

    public RoleCatalog(
        IEnumerable<RoleDefinition> definitions,
        IEnumerable<PermissionDefinition> permissionDefinitions)
    {
        _roleDefinitions = definitions
            .Select(definition => new RoleDefinition(
                definition.Role,
                definition.DisplayName,
                definition.Description,
                definition.Category,
                definition.RiskLevel,
                definition.Permissions.Distinct(StringComparer.Ordinal).ToArray()))
            .OrderBy(definition => definition.Role, StringComparer.Ordinal)
            .ToArray();

        _roles = _roleDefinitions.ToDictionary(
            definition => definition.Role,
            definition => definition.Permissions,
            StringComparer.Ordinal);

        var metadata = permissionDefinitions.ToDictionary(
            definition => definition.Permission,
            StringComparer.Ordinal);
        _permissionDefinitions = _roleDefinitions
            .SelectMany(definition => definition.Permissions)
            .Distinct(StringComparer.Ordinal)
            .Select(permission => metadata.GetValueOrDefault(permission) ??
                new PermissionDefinition(
                    permission,
                    permission,
                    "Permission registered by a module without catalog metadata.",
                    "uncategorized",
                    "medium"))
            .OrderBy(permission => permission.Permission, StringComparer.Ordinal)
            .ToArray();
        CatalogVersion = ComputeCatalogVersion(_roleDefinitions, _permissionDefinitions);
    }

    public IReadOnlyCollection<string> PermissionsFor(string role) =>
        _roles.GetValueOrDefault(role, []);

    public IReadOnlyCollection<RoleDefinition> ListRoles() => _roleDefinitions;

    public IReadOnlyCollection<PermissionDefinition> ListPermissions() => _permissionDefinitions;

    public bool ContainsRole(string role) => _roles.ContainsKey(role);

    private static string ComputeCatalogVersion(
        IReadOnlyCollection<RoleDefinition> roles,
        IReadOnlyCollection<PermissionDefinition> permissions)
    {
        var builder = new StringBuilder();
        foreach (var role in roles)
        {
            builder
                .Append(role.Role).Append('|')
                .Append(role.DisplayName).Append('|')
                .Append(role.Description).Append('|')
                .Append(role.Category).Append('|')
                .Append(role.RiskLevel).Append('|')
                .AppendJoin(',', role.Permissions.OrderBy(permission => permission, StringComparer.Ordinal))
                .AppendLine();
        }

        foreach (var permission in permissions)
        {
            builder
                .Append(permission.Permission).Append('|')
                .Append(permission.DisplayName).Append('|')
                .Append(permission.Description).Append('|')
                .Append(permission.Category).Append('|')
                .Append(permission.RiskLevel)
                .AppendLine();
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
