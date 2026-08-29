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
        var permissions = await roleCatalog.PermissionsForAsync(
            tenantId.Value,
            roles,
            cancellationToken);
        return permissions.Contains(permission, StringComparer.Ordinal);
    }
}
