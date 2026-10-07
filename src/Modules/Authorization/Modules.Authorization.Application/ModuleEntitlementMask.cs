using Modules.Tenancy.Domain;

namespace Modules.Authorization.Application;

/// <summary>
/// Descarta los permisos cuyo módulo no está efectivamente habilitado (spec 2026-10-07, «Decisión»).
/// Como cada endpoint exige su permiso con <c>RequireClaim</c> y cada handler lo revalida por claims,
/// un permiso enmascarado es un 403 en las dos capas, y la SPA lo esconde sola.
///
/// Indexa las <see cref="PermissionDefinition"/> **registradas** en el contenedor, no
/// <c>IRoleCatalog.ListPermissions()</c>: esa lista sólo trae lo que concede algún rol de sistema, y
/// un permiso que llega por un rol custom o por <c>X-Permissions</c> quedaría sin índice. Un permiso
/// no registrado, o registrado sin módulos (<c>null</c>), se enmascara. Puro y singleton.
/// </summary>
public sealed class ModuleEntitlementMask(IEnumerable<PermissionDefinition> registered)
{
    private readonly Dictionary<string, IReadOnlyCollection<TenantModuleKey>?> _requiredModules =
        registered.ToDictionary(
            definition => definition.Permission,
            definition => definition.RequiredModules,
            StringComparer.Ordinal);

    public bool Allows(string permission, TenantModuleSet modules) =>
        _requiredModules.TryGetValue(permission, out var required) &&
        required is not null &&
        required.All(modules.IsEnabled);

    public IReadOnlyCollection<string> Apply(IEnumerable<string> permissions, TenantModuleSet modules) =>
        permissions
            .Where(permission => Allows(permission, modules))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
}
