using BuildingBlocks.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.ServiceContracts.Application;

/// <summary>Copia de <c>PipelineAuthorization</c> (<c>PipelineSupport.cs:10-25</c>): tenant de la ruta distinto del activo,
/// o permiso faltante → 403 <c>authorization.denied</c>, nunca 404. Después, la capacidad <c>billing</c> →
/// 403 <c>tenancy.module_not_enabled</c>. Es defensa en profundidad: con <c>RequiredModules: [ServiceContracts]</c>
/// <c>ModuleEntitlementMask</c> quita el permiso antes, y con el módulo apagado llega un 403 sin código.</summary>
internal static class ServiceContractsAuthorization
{
    public static async Task EnsureAsync(
        IExecutionContext executionContext, ITenantModules tenantModules, Guid tenantId, string permission, CancellationToken cancellationToken)
    {
        if (executionContext.TenantId.Value != tenantId || !executionContext.HasPermission(permission))
        {
            throw Denied();
        }

        await TenantModuleGuard.EnsureEnabledAsync(tenantModules, tenantId, TenantModuleKeys.ServiceContracts, cancellationToken);
    }

    public static RequestForbiddenException Denied() =>
        new("authorization.denied", "The subject cannot perform this billing operation for this tenant.");
}
