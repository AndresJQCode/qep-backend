using Modules.Tenancy.Domain;

namespace Modules.Authorization.Application;

/// <summary>
/// Spec 2026-11-15 (roles asignables): un rol se puede asignar en un tenant si concede al menos un
/// permiso que ese tenant ofrece. «Ofrece» es el mismo filtro de <c>/authorization/catalog</c>: la
/// máscara por módulos (<see cref="ModuleEntitlementMask"/>) y <c>operator.*</c> sólo en el tenant
/// operador (<see cref="OperatorPermissionFilter"/>).
/// </summary>
/// <remarks>
/// La usan el catálogo (el campo <c>assignable</c>) y <c>RoleReferenceValidator</c> (el 422
/// <c>tenancy.membership.role_unavailable</c>). Si cada uno filtrara por su cuenta, la pantalla
/// ofrecería un rol que la API rechaza, o al revés. Con <paramref name="modules"/> en <c>null</c>
/// (tenant ausente de <c>tenancy.tenants</c>, sólo con el stub) no se enmascara nada. Pura.
/// </remarks>
public static class RoleAssignability
{
    public static IReadOnlyCollection<string> OfferedPermissions(
        IEnumerable<string> rolePermissions,
        TenantModuleSet? modules,
        bool isOperatorTenant,
        ModuleEntitlementMask mask) =>
        rolePermissions
            .Where(permission => modules is null || mask.Allows(permission, modules))
            .Where(permission => isOperatorTenant || !OperatorPermissionFilter.IsOperatorPermission(permission))
            .ToArray();

    public static bool IsAssignable(
        IEnumerable<string> rolePermissions,
        TenantModuleSet? modules,
        bool isOperatorTenant,
        ModuleEntitlementMask mask) =>
        OfferedPermissions(rolePermissions, modules, isOperatorTenant, mask).Count > 0;
}
