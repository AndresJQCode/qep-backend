using System.Security.Cryptography;
using System.Text;
using Modules.Tenancy.Domain;

namespace Modules.Authorization.Application;

/// <summary>
/// Un rol de sistema y los permisos que concede: se versiona con el código y se registra en tiempo de composición. Hoy sólo
/// hay uno, <c>admin</c> (spec 2026-11-25, roles por tenant). Los demás roles son filas de cada tenant; los que trae de
/// fábrica se siembran a partir de un <see cref="RoleTemplate"/>.
/// </summary>
public sealed record RoleDefinition(
    string Role,
    string DisplayName,
    string Description,
    string Category,
    string RiskLevel,
    IReadOnlyCollection<string> Permissions);

/// <param name="RequiredModules">
/// Los módulos que exige el permiso, en conjunción (spec 2026-10-07, «Mapa permiso → módulo»).
/// <c>[]</c> es núcleo. <c>null</c> es «sin mapear» y sólo lo produce el fallback de metadata de
/// <see cref="RoleCatalog"/>: un permiso sin mapear se enmascara siempre. Posicional y sin default
/// para que el compilador obligue a declararlo donde nace el permiso. No entra en
/// <c>CatalogVersion</c>: el hash describe el catálogo del build, no el de un tenant.
/// </param>
public sealed record PermissionDefinition(
    string Permission,
    string DisplayName,
    string Description,
    string Category,
    string RiskLevel,
    IReadOnlyCollection<TenantModuleKey>? RequiredModules)
{
    /// <summary>
    /// Entidad y acción del permiso en la matriz de roles (spec 2026-11-16). Propiedad y no posicional
    /// para no tocar cada construcción de las pruebas; que nadie la olvide lo verifica
    /// <c>CompositionRootTests.EveryRegisteredPermissionDeclaresItsShape</c>. <c>null</c> sólo en el
    /// fallback de metadata de <see cref="RoleCatalog"/>.
    /// </summary>
    public PermissionShape? Shape { get; init; }
}

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
                    "medium",
                    RequiredModules: null))
            .OrderBy(permission => permission.Permission, StringComparer.Ordinal)
            .ToArray();
        CatalogVersion = ComputeCatalogVersion(
            _roleDefinitions,
            _permissionDefinitions,
            PermissionVocabulary.ColumnActions,
            PermissionVocabulary.ActionLabels,
            PermissionVocabulary.CategoryLabels);
    }

    public IReadOnlyCollection<string> PermissionsFor(string role) =>
        _roles.GetValueOrDefault(role, []);

    public IReadOnlyCollection<RoleDefinition> ListRoles() => _roleDefinitions;

    public IReadOnlyCollection<PermissionDefinition> ListPermissions() => _permissionDefinitions;

    public bool ContainsRole(string role) => _roles.ContainsKey(role);

    public static string ComputeCatalogVersion(
        IReadOnlyCollection<RoleDefinition> roles,
        IReadOnlyCollection<PermissionDefinition> permissions,
        IReadOnlyList<string> columnActions,
        IReadOnlyDictionary<string, string> actionLabels,
        IReadOnlyDictionary<string, string> categoryLabels)
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
                .Append(permission.RiskLevel).Append('|')
                // Spec 2026-11-16: mover un permiso de fila o de columna de la matriz cambia el catálogo.
                .Append(permission.Shape?.Entity).Append('|')
                .Append(permission.Shape?.EntityLabel).Append('|')
                .Append(permission.Shape?.Action)
                .AppendLine();
        }

        // Spec 2026-11-16: el vocabulario de la matriz (qué acciones son columna y cómo se llaman las
        // acciones y categorías en uso) también define lo que ve el cliente, aunque ningún permiso cambie.
        builder.AppendJoin(',', columnActions).AppendLine();
        var usedActions = permissions
            .Where(permission => permission.Shape is not null)
            .Select(permission => permission.Shape!.Action);
        foreach (var action in usedActions.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            builder.Append(action).Append('=').Append(actionLabels.GetValueOrDefault(action)).AppendLine();
        }

        foreach (var category in permissions.Select(permission => permission.Category)
                     .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            builder.Append(category).Append('=').Append(categoryLabels.GetValueOrDefault(category)).AppendLine();
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
