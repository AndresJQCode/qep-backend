using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Storage.Application;

// Handler-level authorization (defense in depth beyond the endpoint policy): the caller's
// active tenant must match the target tenant, and the required permission must be present.
// This enforces the cross-tenant isolation invariant inside the use case.
internal static class StorageAuthorization
{
    public static void EnsureAuthorized(
        IExecutionContext executionContext,
        Guid tenantId,
        string permission)
    {
        if (executionContext.TenantId.Value != tenantId
            || !executionContext.HasPermission(permission))
        {
            throw new RequestForbiddenException(
                "authorization.denied",
                "The subject cannot perform this storage operation for this tenant.");
        }
    }
}
