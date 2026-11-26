using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Shopify.Application;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-11-24 §11.1.4: <see cref="IExternalOrderDeliveryCheck"/> sobre Shopify. Vive en el composition root porque Quotations
/// no referencia Shopify ni al revés (<c>ShopifyLayerTests</c>). Usa las reglas del envío (<see cref="ShopifyShippingAddressRules"/>),
/// las mismas que el worker, y sólo para un pedido con fila en el espejo: sin ella no hay a quién empujar la dirección.
/// </summary>
internal sealed class ExternalOrderDeliveryCheck(IShopifyOrderMirrorRepository mirrors, IShopifyGeographyLookup geography)
    : IExternalOrderDeliveryCheck
{
    public async Task<string?> CheckAsync(
        Guid tenantId, Guid orderId, string country, Guid? regionId, CancellationToken cancellationToken)
    {
        if (await mirrors.FindByQepOrderIdAsync(tenantId, orderId, cancellationToken) is null)
        {
            return null;
        }

        var region = regionId is { } id ? await geography.DescribeRegionAsync(id, cancellationToken) : null;
        // Una región de otro país no es la de esta dirección, igual que al mandar.
        var regionCode = region is { } found && string.Equals(found.CountryCode, country, StringComparison.OrdinalIgnoreCase)
            ? found.Code
            : null;
        return ShopifyShippingAddressRules.Check(country, regionCode) switch
        {
            ShopifyShippingAddressIssue.None => null,
            ShopifyShippingAddressIssue.RegionRequired => OrderDeliveryErrors.ShopifyRegionRequired,
            ShopifyShippingAddressIssue.RegionUnmapped => OrderDeliveryErrors.ShopifyRegionUnmapped,
            var issue => throw new ArgumentOutOfRangeException(nameof(regionId), issue, "Unknown shipping address issue."),
        };
    }
}
