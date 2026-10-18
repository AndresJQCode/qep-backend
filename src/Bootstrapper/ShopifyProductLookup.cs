using Modules.Catalog.Application;
using Modules.Catalog.Domain;
using Modules.Shopify.Application;

namespace Bootstrapper;

/// <summary>Spec 2026-10-15 §6.2.2 (H11): Catalog → Shopify, como <see cref="PosProductLookup"/>: el producto por código exacto y
/// el porcentaje de su tasa leído por id, una lectura por tasa distinta. Un pedido trae pocas líneas, así que va una lectura por
/// SKU.</summary>
internal sealed class ShopifyProductLookup(IProductRepository products, ITaxRateRepository taxRates) : IShopifyProductLookup
{
    public async Task<IReadOnlyDictionary<string, ShopifyProductRef>> FindByCodesAsync(
        Guid tenantId, IReadOnlyCollection<string> skus, CancellationToken cancellationToken)
    {
        var found = new Dictionary<string, ShopifyProductRef>(StringComparer.Ordinal);
        var percentages = new Dictionary<TaxRateId, int>();
        foreach (var sku in skus.Where(sku => !string.IsNullOrWhiteSpace(sku)).Distinct(StringComparer.Ordinal))
        {
            var product = await products.FindByCodeAsync(tenantId, sku, cancellationToken);
            if (product is null)
            {
                continue;
            }

            var percentage = 0;
            if (product.TaxRateId is { } rateId)
            {
                if (!percentages.TryGetValue(rateId, out percentage))
                {
                    // Sin tasa, o con una inexistente, cotiza con 0 % (QuotationProductPricingResolver).
                    percentage = (await taxRates.FindAsync(tenantId, rateId, cancellationToken))?.Percentage ?? 0;
                    percentages[rateId] = percentage;
                }
            }

            found[sku] = new ShopifyProductRef(product.Id.Value, product.Code, product.IsActive, percentage);
        }

        return found;
    }
}
