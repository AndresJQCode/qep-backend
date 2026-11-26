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

    /// <summary>
    /// Para lo que comparten dos módulos independientes (los envíos de cotizaciones y de pedidos): basta con que
    /// <b>uno</b> esté efectivo. Con el tenant simulado por el stub no bloquea, igual que <see cref="EnsureEnabledAsync"/>.
    /// El mensaje nombra todos los módulos, porque ninguno solo es «el que falta».
    /// </summary>
    public static async Task EnsureAnyEnabledAsync(
        ITenantModules modules,
        Guid tenantId,
        IReadOnlyCollection<TenantModuleKey> keys,
        CancellationToken cancellationToken)
    {
        var set = await modules.FindAsync(tenantId, cancellationToken);
        if (set is not null && !keys.Any(set.IsEnabled))
        {
            throw new RequestForbiddenException(
                ModuleNotEnabledCode,
                $"None of the '{string.Join("', '", keys.Select(key => key.Value))}' modules is enabled for this tenant.");
        }
    }

    /// <summary>
    /// Igual que <see cref="EnsureEnabledAsync"/> pero un tenant sin fila (<c>FindAsync</c> en <c>null</c>) también se
    /// niega: lo usan los caminos que no pueden confiar en el stub de desarrollo, como las claves de API.
    /// </summary>
    public static async Task EnsureEnabledFailClosedAsync(
        ITenantModules modules,
        Guid tenantId,
        TenantModuleKey key,
        CancellationToken cancellationToken)
    {
        var set = await modules.FindAsync(tenantId, cancellationToken);
        if (set is null || !set.IsEnabled(key))
        {
            throw new RequestForbiddenException(
                ModuleNotEnabledCode,
                $"The '{key.Value}' module is not enabled for this tenant.");
        }
    }
}
