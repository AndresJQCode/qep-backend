using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

// Defensa en profundidad, igual que CatalogAuthorization/CustomersAuthorization: la política del
// endpoint ya frena a quien le falta el permiso, pero no al que lo tiene para otro tenant. 403 y
// nunca 404 — un 404 confirmaría que la cotización existe en otro tenant.
internal static class QuotationsAuthorization
{
    public static void EnsureAuthorized(
        IExecutionContext executionContext, Guid tenantId, string permission)
    {
        if (executionContext.TenantId.Value != tenantId ||
            !executionContext.HasPermission(permission))
        {
            throw new RequestForbiddenException(
                "authorization.denied",
                "The subject cannot perform this quotation operation for this tenant.");
        }
    }

    /// <summary>Lo mismo con "cualquiera de" varios permisos: la contraparte de una política con
    /// <c>RequireClaim</c> de varios valores.</summary>
    public static void EnsureAuthorizedAny(
        IExecutionContext executionContext, Guid tenantId, params string[] permissions)
    {
        if (executionContext.TenantId.Value != tenantId ||
            !permissions.Any(executionContext.HasPermission))
        {
            throw new RequestForbiddenException(
                "authorization.denied",
                "The subject cannot perform this quotation operation for this tenant.");
        }
    }
}
