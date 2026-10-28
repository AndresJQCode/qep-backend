using Modules.Catalog.Application;
using Modules.Catalog.Domain;
using Modules.Shopify.Application;

namespace Bootstrapper;

/// <summary>Spec 2026-10-15 §6.2.2 (H11), con variantes (2026-10-20 §2.9): Catalog → Shopify, como <see cref="PosProductLookup"/>:
/// la **variante** por <c>Code</c> exacto y sensible a mayúsculas —el SKU de la línea de Shopify—, y el porcentaje de la tasa de
/// su producto leído por id, una lectura por tasa distinta. Un pedido trae pocas líneas, así que va una lectura por SKU.</summary>
internal sealed class ShopifyProductLookup(IProductRepository products, ITaxRateRepository taxRates) : IShopifyProductLookup
{
    public async Task<IReadOnlyDictionary<string, ShopifyProductRef>> FindByCodesAsync(
        Guid tenantId, IReadOnlyCollection<string> skus, CancellationToken cancellationToken)
    {
        var found = new Dictionary<string, ShopifyProductRef>(StringComparer.Ordinal);
        var percentages = new Dictionary<TaxRateId, int>();
        foreach (var sku in skus.Where(sku => !string.IsNullOrWhiteSpace(sku)).Distinct(StringComparer.Ordinal))
        {
            var product = await products.FindVariantByCodeAsync(tenantId, sku, cancellationToken);

            // Ordinal, como el índice único: el repositorio ya compara así, y volver a mirar acá deja
            // la regla escrita donde se elige la variante y no sólo en la consulta.
            var variant = product?.Variants.SingleOrDefault(candidate => string.Equals(candidate.Code, sku, StringComparison.Ordinal));
            if (product is null || variant is null)
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

            found[sku] = new ShopifyProductRef(
                variant.Id.Value,
                product.Id.Value,
                variant.Code,
                CatalogVariantReads.IsSellable(product, variant),
                percentage);
        }

        return found;
    }
}
