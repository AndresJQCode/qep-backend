using BuildingBlocks.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Hr.Application;

/// <summary>Copia de <c>LoansAuthorization</c> con una lista de permisos: tenant de la ruta distinto del activo, o <b>cualquier</b>
/// permiso faltante → 403 <c>authorization.denied</c>, nunca 404. Después, la capacidad <c>hr</c> → 403
/// <c>tenancy.module_not_enabled</c>. Es defensa en profundidad: con <c>RequiredModules: [Hr]</c> <c>ModuleEntitlementMask</c> quita el
/// permiso antes, y con el módulo apagado llega un 403 sin código.</summary>
internal static class HrAuthorization
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

        await TenantModuleGuard.EnsureEnabledAsync(tenantModules, tenantId, TenantModuleKeys.Hr, cancellationToken);
    }

    public static RequestForbiddenException Denied() =>
        new("authorization.denied", "The subject cannot perform this hr operation for this tenant.");
}
