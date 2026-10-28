using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Authorization.Application;

public sealed class RolePermissionChecker(ITenantRoleCatalog roleCatalog)
    : IRolePermissionChecker
{
    public async Task<bool> AnyGrantsAsync(
        TenantId tenantId,
        IReadOnlyCollection<string> roles,
        string permission,
        CancellationToken cancellationToken)
    {
        // Lee el catálogo de roles crudo: no pasa por OperatorPermissionFilter, así que el admin de
        // cualquier tenant "tiene" operator.*. No la uses para decidir acceso de operador; eso va por
        // los permisos efectivos (AuthorizationService) y por IOperatorTenant.
        var permissions = await roleCatalog.PermissionsForAsync(
            tenantId.Value,
            roles,
            cancellationToken);
        return permissions.Contains(permission, StringComparer.Ordinal);
    }
}
