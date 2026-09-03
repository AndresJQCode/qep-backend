using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Reporting.Application;

// Autorización a nivel handler, defensa en profundidad más allá de la política del endpoint:
// el tenant activo del llamador tiene que coincidir con el de la ruta, y el permiso estar
// presente. Copia exacta del criterio de CatalogAuthorization y StorageAuthorization.
//
// Devolver 403 y no 404 es deliberado: un 404 confirmaría que el tenant de la ruta existe.
internal static class ReportingAuthorization
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
                "The subject cannot perform this reporting operation for this tenant.");
        }
    }
}
