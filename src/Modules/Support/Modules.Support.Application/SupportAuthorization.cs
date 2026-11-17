using BuildingBlocks.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Support.Application;

/// <summary>Copia de <c>LoansAuthorization</c>: tenant de la ruta distinto del activo, o permiso faltante → 403
/// <c>authorization.denied</c>, nunca 404. Después, la capacidad <c>support</c> → 403 <c>tenancy.module_not_enabled</c>. Es defensa
/// en profundidad: con <c>RequiredModules: [Support]</c> <c>ModuleEntitlementMask</c> quita el permiso antes, y con el módulo apagado
/// llega un 403 sin código.</summary>
internal static class SupportAuthorization
{
    public static Task EnsureAsync(
        IExecutionContext executionContext, ITenantModules tenantModules, Guid tenantId, string permission, CancellationToken cancellationToken) =>
        EnsureAnyAsync(executionContext, tenantModules, tenantId, [permission], cancellationToken);

    /// <summary>La misma regla que la política <see cref="SupportPolicies.TicketView"/>: leer o gestionar.</summary>
    public static Task EnsureCanViewAsync(
        IExecutionContext executionContext, ITenantModules tenantModules, Guid tenantId, CancellationToken cancellationToken) =>
        EnsureAnyAsync(executionContext, tenantModules, tenantId, [SupportPermissions.Read, SupportPermissions.Manage], cancellationToken);

    /// <summary>La misma regla que la política <see cref="SupportPolicies.SettingsView"/>: leer, gestionar o configurar.</summary>
    public static Task EnsureCanViewSettingsAsync(
        IExecutionContext executionContext, ITenantModules tenantModules, Guid tenantId, CancellationToken cancellationToken) =>
        EnsureAnyAsync(
            executionContext,
            tenantModules,
            tenantId,
            [SupportPermissions.Read, SupportPermissions.Manage, SupportPermissions.SettingsManage],
            cancellationToken);

    public static RequestForbiddenException Denied() =>
        new("authorization.denied", "The subject cannot perform this support operation for this tenant.");

    private static async Task EnsureAnyAsync(
        IExecutionContext executionContext,
        ITenantModules tenantModules,
        Guid tenantId,
        IReadOnlyList<string> permissions,
        CancellationToken cancellationToken)
    {
        if (executionContext.TenantId.Value != tenantId || !permissions.Any(executionContext.HasPermission))
        {
            throw Denied();
        }

        await TenantModuleGuard.EnsureEnabledAsync(tenantModules, tenantId, TenantModuleKeys.Support, cancellationToken);
    }
}
