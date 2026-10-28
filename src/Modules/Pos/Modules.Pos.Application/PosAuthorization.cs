using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Pos.Application;

/// <summary>
/// Copia de CompaniesAuthorization: tenant de la ruta distinto del activo, o permiso faltante →
/// 403. Nunca 404: confirmaría que el id existe en otro tenant.
/// </summary>
internal static class PosAuthorization
{
    public static void EnsureAuthorized(IExecutionContext executionContext, Guid tenantId, string permission)
    {
        if (executionContext.TenantId.Value != tenantId || !executionContext.HasPermission(permission))
        {
            throw Denied();
        }
    }

    public static RequestForbiddenException Denied() =>
        new("authorization.denied", "The subject cannot perform this pos operation for this tenant.");
}
