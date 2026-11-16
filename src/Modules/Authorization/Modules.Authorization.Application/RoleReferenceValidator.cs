using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Authorization.Application;

/// <remarks>
/// Spec 2026-11-15: «asignable» sale de <see cref="RoleAssignability"/>, la misma función con la que
/// <c>/authorization/catalog</c> marca <c>assignable</c>. Los permisos salen del catálogo del tenant
/// (sistema y custom), así que un rol custom de sólo <c>pos.*</c> tampoco se asigna sin <c>pos</c>.
/// </remarks>
public sealed class RoleReferenceValidator(
    ITenantRoleCatalog roleCatalog,
    ITenantModules tenantModules,
    IOperatorTenant operatorTenant,
    ModuleEntitlementMask entitlementMask)
    : IRoleReferenceValidator
{
    public async Task<RoleReference> CheckAsync(
        TenantId tenantId,
        string role,
        CancellationToken cancellationToken)
    {
        if (!await roleCatalog.ContainsRoleAsync(tenantId.Value, role, cancellationToken))
        {
            return RoleReference.Unknown;
        }

        var permissions = await roleCatalog.PermissionsForAsync(tenantId.Value, [role], cancellationToken);
        var modules = await tenantModules.FindAsync(tenantId.Value, cancellationToken);
        return RoleAssignability.IsAssignable(
                permissions, modules, operatorTenant.IsOperator(tenantId.Value), entitlementMask)
            ? RoleReference.Known
            : RoleReference.Unassignable;
    }
}
