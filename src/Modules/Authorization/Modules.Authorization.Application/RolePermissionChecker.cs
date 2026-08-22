using Modules.Tenancy.Application;

namespace Modules.Authorization.Application;

public sealed class RolePermissionChecker(IRoleCatalog roleCatalog) : IRolePermissionChecker
{
    public bool AnyGrants(IReadOnlyCollection<string> roles, string permission) =>
        roles
            .SelectMany(roleCatalog.PermissionsFor)
            .Contains(permission, StringComparer.Ordinal);
}
