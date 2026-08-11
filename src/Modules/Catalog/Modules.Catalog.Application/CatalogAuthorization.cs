using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Catalog.Application;

// Handler-level authorization, defense in depth beyond the endpoint policy: the caller
// active tenant must match the tenant in the route, and the permission must be present.
// Returning 403 rather than 404 is deliberate: a 404 would leak whether another tenant
// catalogue holds that id.
internal static class CatalogAuthorization
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
                "The subject cannot perform this catalog operation for this tenant.");
        }
    }
}
