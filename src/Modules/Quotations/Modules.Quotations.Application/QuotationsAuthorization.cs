using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

// Defensa en profundidad, igual que CatalogAuthorization/CustomersAuthorization: la política del
// endpoint ya frena a quien le falta el permiso, pero no al que lo tiene para otro tenant. 403 y
// nunca 404 — un 404 confirmaría que la cotización existe en otro tenant.
//
// TEMPORAL (a pedido, 2026-08-24): la restricción por permiso queda desactivada mientras se
// prueba el flujo manualmente sin tener que armar X-Permissions en cada request. El aislamiento
// de tenant NO se toca — eso no es una restricción de rol/permiso, es aislamiento de datos entre
// tenants. Reactivar la línea comentada antes de producción.
internal static class QuotationsAuthorization
{
    public static void EnsureAuthorized(
        IExecutionContext executionContext, Guid tenantId, string permission)
    {
        if (executionContext.TenantId.Value != tenantId /* || !executionContext.HasPermission(permission) */)
        {
            throw new RequestForbiddenException(
                "authorization.denied",
                "The subject cannot perform this quotation operation for this tenant.");
        }
    }
}
