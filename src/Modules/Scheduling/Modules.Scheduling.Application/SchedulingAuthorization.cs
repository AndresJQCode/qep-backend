using BuildingBlocks.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Scheduling.Application;

/// <summary>Copia de <c>HrAuthorization</c>: tenant de la ruta distinto del activo, o <b>cualquier</b> permiso faltante → 403
/// <c>authorization.denied</c>, nunca 404. Después, la capacidad <c>scheduling</c> → 403 <c>tenancy.module_not_enabled</c>. Es defensa en
/// profundidad: con <c>RequiredModules: [Scheduling]</c> <c>ModuleEntitlementMask</c> quita el permiso antes, y con el módulo apagado
/// llega un 403 sin código. El <see cref="ResourceScope"/> de quien pide se resuelve aparte, contra el repositorio de recursos.</summary>
internal static class SchedulingAuthorization
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

        await TenantModuleGuard.EnsureEnabledAsync(tenantModules, tenantId, TenantModuleKeys.Scheduling, cancellationToken);
    }

    /// <summary>Quien tiene «ver todas las agendas» actúa sobre todos los recursos; el resto, sólo sobre los vinculados a su membresía.</summary>
    public static bool CanSeeEveryAgenda(IExecutionContext executionContext) =>
        executionContext.HasPermission(SchedulingPermissions.AgendaReadAll);

    public static RequestForbiddenException Denied() =>
        new("authorization.denied", "The subject cannot perform this scheduling operation for this tenant.");
}
