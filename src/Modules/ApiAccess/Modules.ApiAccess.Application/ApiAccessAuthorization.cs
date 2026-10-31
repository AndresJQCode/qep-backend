using BuildingBlocks.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.ApiAccess.Application;

// Defensa en profundidad más allá de la política del endpoint: el tenant activo del llamador tiene
// que coincidir con el de la ruta, el permiso estar presente y el módulo developer prendido.
// Responde 403 y no 404: un 404 confirmaría que el id existe en otro tenant.
internal static class ApiAccessAuthorization
{
    public static async Task EnsureAsync(
        IExecutionContext executionContext,
        ITenantModules tenantModules,
        Guid tenantId,
        string permission,
        CancellationToken cancellationToken)
    {
        if (executionContext.TenantId.Value != tenantId || !executionContext.HasPermission(permission))
        {
            throw new RequestForbiddenException(
                "authorization.denied",
                "The subject cannot perform this api access operation for this tenant.");
        }

        await TenantModuleGuard.EnsureEnabledAsync(
            tenantModules, tenantId, TenantModuleKeys.Developer, cancellationToken);
    }
}
