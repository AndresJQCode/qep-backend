using Modules.Quotations.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// El módulo <c>projects</c> de las cotizaciones (spec 2026-11-26 §6.1 y §7, P9): apagado, una cotización de proyecto
/// no se lee ni se toca, y no se crea. Va después del permiso y de cargar el documento, así que un 404 sigue siendo
/// 404. Ningún caso de uso de pedidos pasa por aquí: un pedido de proyecto que ya existe se opera siempre.
/// </summary>
internal static class ProjectModuleGuard
{
    /// <summary><c>403 tenancy.module_not_enabled</c> si el documento es de proyecto y el módulo está apagado. Uno
    /// <c>Standard</c> no consulta los módulos.</summary>
    public static Task EnsureAsync(
        ITenantModules modules, Guid tenantId, QuotationKind kind, CancellationToken cancellationToken) =>
        kind == QuotationKind.Project
            ? TenantModuleGuard.EnsureEnabledAsync(modules, tenantId, TenantModuleKeys.Projects, cancellationToken)
            : Task.CompletedTask;

    /// <summary>Con el tenant simulado por el stub es <c>true</c>, como en <see cref="TenantModuleGuard"/>.</summary>
    public static async Task<bool> IsEnabledAsync(
        ITenantModules modules, Guid tenantId, CancellationToken cancellationToken)
    {
        var set = await modules.FindAsync(tenantId, cancellationToken);
        return set is null || set.IsEnabled(TenantModuleKeys.Projects);
    }
}
