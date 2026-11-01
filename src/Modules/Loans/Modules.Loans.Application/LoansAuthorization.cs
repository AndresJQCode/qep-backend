using BuildingBlocks.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Loans.Application;

/// <summary>Copia de <c>ServiceContractsAuthorization</c>: tenant de la ruta distinto del activo, o permiso faltante →
/// 403 <c>authorization.denied</c>, nunca 404. Después, la capacidad <c>loans</c> → 403 <c>tenancy.module_not_enabled</c>. Es defensa
/// en profundidad: con <c>RequiredModules: [Loans]</c> <c>ModuleEntitlementMask</c> quita el permiso antes, y con el módulo apagado
/// llega un 403 sin código.</summary>
internal static class LoansAuthorization
{
    public static async Task EnsureAsync(
        IExecutionContext executionContext, ITenantModules tenantModules, Guid tenantId, string permission, CancellationToken cancellationToken)
    {
        if (executionContext.TenantId.Value != tenantId || !executionContext.HasPermission(permission))
        {
            throw Denied();
        }

        await TenantModuleGuard.EnsureEnabledAsync(tenantModules, tenantId, TenantModuleKeys.Loans, cancellationToken);
    }

    public static RequestForbiddenException Denied() =>
        new("authorization.denied", "The subject cannot perform this loans operation for this tenant.");
}