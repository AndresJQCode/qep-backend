using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Catalog.Application;

// Autorización a nivel handler, defensa en profundidad más allá de la política del endpoint:
// el tenant activo del llamador tiene que coincidir con el de la ruta, y el permiso estar presente.
// Devolver 403 y no 404 es deliberado: un 404 filtraría si el catálogo de otro tenant
// tiene ese id.
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
