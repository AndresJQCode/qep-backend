using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Authorization.Application;

public sealed class RoleReferenceValidator(ITenantRoleCatalog roleCatalog)
    : IRoleReferenceValidator
{
    public Task<bool> IsKnownRoleAsync(
        TenantId tenantId,
        string role,
        CancellationToken cancellationToken) =>
        roleCatalog.ContainsRoleAsync(tenantId.Value, role, cancellationToken);
}
