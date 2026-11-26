using Modules.Tenancy.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Cotizaciones y pedidos son módulos independientes (un pedido nace sin cotización, spec 2026-10-15), pero comparten la
/// configuración de envíos, el contenido del PDF y los canales de WhatsApp. Un handler de esa configuración exige que
/// <b>alguno</b> de los dos esté efectivo (<c>TenantModuleGuard.EnsureAnyEnabledAsync</c>): exigir sólo
/// <c>quotations</c> dejaba sin transportadoras ni fletes al tenant que oculta cotizaciones y vende por pedidos.
/// </summary>
internal static class QuotationsModuleSets
{
    public static readonly IReadOnlyList<TenantModuleKey> QuotationsOrOrders =
        [TenantModuleKeys.Quotations, TenantModuleKeys.Orders];
}
