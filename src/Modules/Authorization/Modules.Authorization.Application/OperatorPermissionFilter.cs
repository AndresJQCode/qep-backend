namespace Modules.Authorization.Application;

/// <summary>
/// Spec 2026-10-08 §2: quita todo permiso <c>operator.*</c> fuera del tenant operador. Se aplica en los
/// mismos lugares que <see cref="ModuleEntitlementMask"/> y justo después de él: la cookie real
/// (<see cref="AuthorizationService"/>), el stub, <c>/authorization/catalog</c> y
/// <c>/authorization/roles</c>. Comparación ordinal, como todo permiso del repo. Puro.
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
