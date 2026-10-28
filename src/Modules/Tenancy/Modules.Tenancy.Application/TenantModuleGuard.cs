using BuildingBlocks.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

/// <summary>
/// El chequeo explícito para los lugares cuyo permiso es de núcleo pero lo que exponen es de un
/// módulo (spec 2026-10-07): la configuración del Excel de pedidos y los archivos de Storage según
/// su dueño. 403 <c>tenancy.module_not_enabled</c> por <c>ApiExceptionHandler</c>. Con el tenant
/// simulado por el stub (<c>FindAsync</c> en <c>null</c>) no bloquea.
/// </summary>
public static class TenantModuleGuard
{
    public const string ModuleNotEnabledCode = "tenancy.module_not_enabled";

    public static async Task EnsureEnabledAsync(
        ITenantModules modules,
        Guid tenantId,
        TenantModuleKey key,
        CancellationToken cancellationToken)
    {
        var set = await modules.FindAsync(tenantId, cancellationToken);
        if (set is not null && !set.IsEnabled(key))
        {
            throw new RequestForbiddenException(
                ModuleNotEnabledCode,
                $"The '{key.Value}' module is not enabled for this tenant.");
        }
    }
}
