using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Wholesale.Application;

/// <summary>
/// Copia de <c>PurchasesAuthorization</c>: tenant de la ruta distinto del activo, o permiso faltante → 403
/// <c>authorization.denied</c>, nunca 404 (un 404 diría si la lista o el distribuidor de otro tenant existe). Con el módulo
/// <c>wholesale</c> apagado, <c>ModuleEntitlementMask</c> ya quitó el permiso antes de llegar acá. Corre antes de tocar
/// cualquier puerto: es la segunda capa del aislamiento de tenant, después de la política de la ruta.
/// </summary>
internal static class WholesaleAuthorization
{
    public static void EnsureAuthorized(IExecutionContext executionContext, Guid tenantId, string permission)
    {
        if (executionContext.TenantId.Value != tenantId || !executionContext.HasPermission(permission))
        {
            throw Denied();
        }
    }

    public static RequestForbiddenException Denied() =>
        new("authorization.denied", "The subject cannot perform this wholesale operation for this tenant.");
}
