namespace Modules.Authorization.Application;

/// <summary>
/// Spec 2026-10-08 §2: quita todo permiso <c>operator.*</c> fuera del tenant operador. Se aplica justo
/// después de <see cref="ModuleEntitlementMask"/> en la cookie real (<see cref="AuthorizationService"/>),
/// el stub y <c>/authorization/catalog</c>; y además en <c>/authorization/roles</c>, que no pasa por el
/// enmascarado. Comparación ordinal, como todo permiso del repo. Puro.
/// </summary>
public static class OperatorPermissionFilter
{
    // Acá y no en OperatorPermissions: CompositionRootTests toma toda constante de una clase
    // *Permissions como un permiso.
    public const string Prefix = "operator.";

    public static bool IsOperatorPermission(string permission) =>
        permission.StartsWith(Prefix, StringComparison.Ordinal);

    public static IReadOnlyCollection<string> Apply(IEnumerable<string> permissions, bool isOperatorTenant) =>
        isOperatorTenant
            ? permissions.ToArray()
            : permissions.Where(permission => !IsOperatorPermission(permission)).ToArray();
}
