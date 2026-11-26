using BuildingBlocks.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Ai.Application;

/// <summary>Spec 2026-11-17 (copiloto), D-C14 y plan fase 2 «Ajustes»: <c>AiAuthorization</c> exige la capacidad <c>ai</c>, que depende
/// de Mensajería; el copiloto se gatea con la suya. Tenant de la ruta distinto del activo, o permiso faltante → 403
/// <c>authorization.denied</c>, nunca 404. Después, la capacidad <c>copilot</c> → 403 <c>tenancy.module_not_enabled</c>.</summary>
internal static class CopilotAuthorization
{
    public static async Task EnsureAsync(
        IExecutionContext executionContext, ITenantModules tenantModules, Guid tenantId, string permission, CancellationToken cancellationToken)
    {
        if (executionContext.TenantId.Value != tenantId || !executionContext.HasPermission(permission))
        {
            throw AiAuthorization.Denied();
        }

        await TenantModuleGuard.EnsureEnabledAsync(tenantModules, tenantId, TenantModuleKeys.Copilot, cancellationToken);
    }
}
