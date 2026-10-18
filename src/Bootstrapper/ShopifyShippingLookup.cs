using Modules.Catalog.Application;
using Modules.Catalog.Domain;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Shopify.Application;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-10-15 §6.2.2 pasos 4 y 5: transportadoras y la tasa del envío de Quotations (H20, H22). Sólo lee: las entidades que
/// trae el repositorio quedan rastreadas sin cambios, y el alta del pedido corre en otro scope (<see cref="ShopifyOrderWriter"/>).
/// </summary>
internal sealed class ShopifyShippingLookup(
    ICarrierRepository carriers, IQuotationSettingsStore settings, ITaxRateRepository taxRates) : IShopifyShippingLookup
{
    public async Task<ShopifyCarrierRef?> MatchCarrierAsync(Guid tenantId, string title, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        // El mismo algoritmo que el índice IX_carrier_names_tenant_name: nombre y externos comparten esa forma normalizada.
        var wanted = CarrierName.Normalize(title);
        var match = (await carriers.ListAsync(tenantId, cancellationToken))
            .Where(carrier => carrier.IsActive)
            .FirstOrDefault(carrier => CarrierName.Normalize(carrier.Name) == wanted
                || carrier.ExternalNames.Any(external => CarrierName.Normalize(external) == wanted));
        return match is null ? null : new ShopifyCarrierRef(match.Id.Value, match.Name, match.SupportsCashOnDelivery);
    }

    public async Task<int?> GetFreightTaxPercentageAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (await settings.GetFreightTaxRateIdAsync(tenantId, cancellationToken) is not { } rateId)
        {
            return null;
        }

        // Inactiva cuenta como faltante, igual que en ShippingResolver: freight_tax_rate_missing.
        var rate = await taxRates.FindAsync(tenantId, new TaxRateId(rateId), cancellationToken);
        return rate is { IsActive: true } ? rate.Percentage : null;
    }
}
