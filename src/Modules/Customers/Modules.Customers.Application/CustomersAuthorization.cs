using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Customers.Application;

// Autorizacion a nivel handler, defensa en profundidad mas alla de la politica del endpoint: el
// tenant activo del llamador tiene que coincidir con el de la ruta, y el permiso estar presente.
// Devolver 403 y no 404 es deliberado: un 404 filtraria si el cliente de otro tenant existe.
internal static class CustomersAuthorization
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
                "The subject cannot perform this customers operation for this tenant.");
        }
    }
}
