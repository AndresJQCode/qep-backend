using Modules.Catalog.Application;
using Modules.Catalog.Domain;
using Modules.Shopify.Application;

namespace Bootstrapper;

/// <summary>Spec 2026-11-13 (D3/D4): Catalog → Shopify, el nombre visible y el código de las variantes que la bandeja lista junto a
/// cada asociación de línea personalizada. Como <c>QuotationProductLookup</c>: los productos del lote en una consulta y las
/// opciones de esos productos en otra. Activas e inactivas; una inexistente o de otro tenant no aparece.</summary>
internal sealed class ShopifyVariantLabels(IProductRepository products, IOptionDefinitionRepository options) : IShopifyVariantLabels
{
    public async Task<IReadOnlyDictionary<Guid, ShopifyVariantLabel>> DescribeAsync(
        Guid tenantId, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken)
    {
        if (variantIds.Count == 0)
        {
            return new Dictionary<Guid, ShopifyVariantLabel>();
        }

        var wanted = variantIds.ToHashSet();
        var loaded = await products.FindVariantsByIdsAsync(
            tenantId, wanted.Select(id => new VariantId(id)).ToArray(), cancellationToken);
        var valueNames = await CatalogVariantReads.LoadValueNamesAsync(options, tenantId, loaded, cancellationToken);

        return CatalogVariantReads.Select(loaded, wanted).ToDictionary(
            pair => pair.Variant.Id.Value,
            pair => new ShopifyVariantLabel(
                pair.Variant.Id.Value,
                CatalogVariantReads.DisplayNameOf(pair.Product, pair.Variant, valueNames),
                pair.Variant.Code,
                CatalogVariantReads.IsSellable(pair.Product, pair.Variant)));
    }
}
