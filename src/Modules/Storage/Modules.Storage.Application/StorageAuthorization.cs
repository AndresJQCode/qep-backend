using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Storage.Application;

// Autorización a nivel handler (defensa en profundidad más allá de la política del endpoint):
// el tenant activo del llamador tiene que coincidir con el destino, y el permiso estar presente.
// Esto hace cumplir el invariante de aislamiento entre tenants dentro del caso de uso.
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
