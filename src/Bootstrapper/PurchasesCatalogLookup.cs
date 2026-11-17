using Modules.Catalog.Application;
using Modules.Catalog.Domain;
using Modules.Purchases.Application;

namespace Bootstrapper;

/// <summary>
/// Catalog → Purchases, por variante: código, nombre visible (<see cref="CatalogVariantReads.DisplayNameOf(Product, ProductVariant, IReadOnlyDictionary{Guid, string})"/>)
/// y tasa de IVA actual, como <see cref="PosProductLookup"/>. No filtra por activo (D-6): comprar para un producto aún no publicado
/// es legítimo. Sin tasa, o con una que ya no existe, la línea va con 0 %.
/// </summary>
internal sealed class PurchasesCatalogLookup(
    IProductRepository products,
    IOptionDefinitionRepository options,
    ITaxRateRepository taxRates)
    : IPurchasesCatalogLookup
{
    public async Task<IReadOnlyDictionary<Guid, PurchasesVariantRef>> FindVariantsAsync(
        Guid tenantId, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken)
    {
        if (variantIds.Count == 0)
        {
            return new Dictionary<Guid, PurchasesVariantRef>();
        }

        var wanted = variantIds.ToHashSet();
        var found = await products.FindVariantsByIdsAsync(
            tenantId, wanted.Select(id => new VariantId(id)).ToArray(), cancellationToken);
        var selected = CatalogVariantReads.Select(found, wanted);
        var valueNames = await CatalogVariantReads.LoadValueNamesAsync(options, tenantId, found, cancellationToken);
        var percentages = await CatalogVariantReads.LoadTaxPercentagesAsync(taxRates, tenantId, found, cancellationToken);

        return selected.ToDictionary(
            pair => pair.Variant.Id.Value,
            pair => new PurchasesVariantRef(
                pair.Variant.Id.Value,
                pair.Variant.Code,
                CatalogVariantReads.DisplayNameOf(pair.Product, pair.Variant, valueNames),
                pair.Product.TaxRateId is { } id && percentages.TryGetValue(id, out var percentage) ? percentage : 0m));
    }
}
