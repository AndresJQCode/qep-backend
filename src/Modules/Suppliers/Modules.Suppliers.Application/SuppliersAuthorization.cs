using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Suppliers.Application;

// Autorización a nivel de handler, defensa en profundidad más allá de la política del endpoint: el
// tenant activo del llamador tiene que coincidir con el de la ruta y el permiso estar presente.
// Responder 403 y no 404 es deliberado: un 404 filtraría si el proveedor de otro tenant existe.
internal static class SuppliersAuthorization
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
                "The subject cannot perform this suppliers operation for this tenant.");
        }
    }
}
