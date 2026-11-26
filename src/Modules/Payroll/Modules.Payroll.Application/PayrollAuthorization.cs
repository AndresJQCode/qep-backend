using BuildingBlocks.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Payroll.Application;

/// <summary>Copia de <c>HrAuthorization</c>: tenant de la ruta distinto del activo, o <b>cualquier</b> permiso faltante → 403
/// <c>authorization.denied</c>, nunca 404. Después, la capacidad <c>payroll</c> → 403 <c>tenancy.module_not_enabled</c>. Es defensa
/// en profundidad: con <c>RequiredModules: [Payroll]</c> <c>ModuleEntitlementMask</c> quita el permiso antes, y con el módulo apagado
/// llega un 403 sin código.</summary>
internal static class PayrollAuthorization
{
    public static async Task EnsureAsync(
        IExecutionContext executionContext,
        ITenantModules tenantModules,
        Guid tenantId,
        CancellationToken cancellationToken,
        params string[] permissions)
    {
        if (executionContext.TenantId.Value != tenantId || !permissions.All(executionContext.HasPermission))
        {
            throw Denied();
        }

        await TenantModuleGuard.EnsureEnabledAsync(tenantModules, tenantId, TenantModuleKeys.Payroll, cancellationToken);
    }

    /// <summary>Igual que <see cref="EnsureAsync"/>, pero basta <b>uno</b> de los permisos (<see cref="PayrollPolicies.SettingsView"/>).</summary>
    public static async Task EnsureAnyAsync(
        IExecutionContext executionContext,
        ITenantModules tenantModules,
        Guid tenantId,
        CancellationToken cancellationToken,
        params string[] anyOf)
    {
        if (executionContext.TenantId.Value != tenantId || !anyOf.Any(executionContext.HasPermission))
        {
            throw Denied();
        }

        await TenantModuleGuard.EnsureEnabledAsync(tenantModules, tenantId, TenantModuleKeys.Payroll, cancellationToken);
    }

    public static RequestForbiddenException Denied() =>
        new("authorization.denied", "The subject cannot perform this payroll operation for this tenant.");
}
