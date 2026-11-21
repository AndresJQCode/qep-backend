using BuildingBlocks.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Coordinadora.Application;

/// <summary>Copia de <c>SiigoAuthorization</c>: tenant de la ruta distinto del activo, o permiso faltante → 403 <c>authorization.denied</c>,
/// nunca 404. Después, el módulo <c>coordinadora</c> apagado → 403 <c>tenancy.module_not_enabled</c>. Es la segunda capa: la política de
/// la ruta ya exige el permiso, y el handler lo revalida antes de tocar el repositorio.</summary>
internal static class CoordinadoraAuthorization
{
    public const string DeniedCode = "authorization.denied";

    public static async Task EnsureAsync(
        IExecutionContext executionContext, ITenantModules tenantModules, Guid tenantId, string permission, CancellationToken cancellationToken)
    {
        if (executionContext.TenantId.Value != tenantId || !executionContext.HasPermission(permission))
        {
            throw Denied();
        }

        await TenantModuleGuard.EnsureEnabledAsync(tenantModules, tenantId, TenantModuleKeys.Coordinadora, cancellationToken);
    }

    public static RequestForbiddenException Denied() =>
        new(DeniedCode, "The subject cannot perform this Coordinadora operation for this tenant.");
}
