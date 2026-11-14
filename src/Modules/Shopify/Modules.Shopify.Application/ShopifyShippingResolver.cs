namespace Modules.Shopify.Application;

/// <summary>§6.2.2 pasos 4 y 5: la transportadora del título de la primera línea y, sólo si el envío se cobra en COP, la tasa del
/// envío del tenant. Con recogida o sin líneas no se pregunta nada. La usan la importación y el planificador de cambios (spec
/// 2026-11-14), con la misma regla (<see cref="ShopifyShippingMapper"/>).</summary>
public sealed class ShopifyShippingResolver(IShopifyShippingLookup shipping)
{
    public async Task<ShopifyShippingResult> ResolveAsync(
        Guid tenantId, ShopifyOrderSnapshot snapshot, string currency, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (ShopifyShippingMapper.IsStorePickup(snapshot))
        {
            return ShopifyShippingMapper.Map(snapshot, currency, null, null);
        }

        var title = ShopifyShippingMapper.CarrierTitle(snapshot);
        var carrier = title is null ? null : await shipping.MatchCarrierAsync(tenantId, title, cancellationToken);
        var rate = ShopifyPricing.IsCop(currency) && ShopifyShippingMapper.Amount(snapshot) != 0m
            ? await shipping.GetFreightTaxPercentageAsync(tenantId, cancellationToken)
            : null;
        return ShopifyShippingMapper.Map(snapshot, currency, carrier, rate);
    }
}
