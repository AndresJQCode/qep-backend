using Modules.Catalog.Application;
using Modules.Catalog.Domain;
using Modules.Quotations.Application;

namespace Bootstrapper;

/// <summary>
/// Adapta el repositorio de <c>Catalog</c> al puerto que <c>quotations</c> declara. Mismo
/// criterio de aislamiento que <see cref="QuotationCustomerLookup"/>.
///
/// No decide nada: sólo traduce la variante y sus escalas de precio al vocabulario de
/// <c>quotations</c>, con **todos** los precios base que la variante guarda (spec 2026-10-20 §4:
/// precio, escalas y empaques son de la variante; la tasa, del producto). Cuál aplica —y qué
/// escala, para una cantidad— es de
/// <c>QuotationDiscountResolver</c>/<c>QuotationProductPricingResolver</c>, en Application.
/// Tampoco filtra inactivas: dice si la variante se vende (<see cref="QuotationVariantPricingRef.IsActive"/>)
/// y el resolver decide, porque revalorizar una línea ya cotizada no se bloquea por una baja.
/// </summary>
internal sealed class QuotationProductPricingLookup(
    IProductRepository repository,
    IOptionDefinitionRepository optionRepository,
    ITaxRateRepository taxRateRepository)
    : IQuotationProductPricingLookup
{
    public async Task<QuotationVariantPricingRef?> FindVariantAsync(
        Guid tenantId, Guid variantId, CancellationToken cancellationToken)
    {
        var found = await FindManyAsync(tenantId, [variantId], cancellationToken);
        return found.GetValueOrDefault(variantId);
    }

    // Una consulta para todas las variantes, una para las opciones y una por tasa distinta:
    // revalorizar una cotización de diez líneas no cuesta veinte lecturas.
    public async Task<IReadOnlyDictionary<Guid, QuotationVariantPricingRef>> FindManyAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> variantIds,
        CancellationToken cancellationToken)
    {
        if (variantIds.Count == 0)
        {
            return new Dictionary<Guid, QuotationVariantPricingRef>();
        }

        var wanted = variantIds.ToHashSet();
        var products = await repository.FindVariantsByIdsAsync(
            tenantId,
            wanted.Select(id => new VariantId(id)).ToArray(),
            cancellationToken);
        var selected = CatalogVariantReads.Select(products, wanted);

        var valueNames = await CatalogVariantReads.LoadValueNamesAsync(
            optionRepository, tenantId, products, cancellationToken);

        // RN-013: la tasa de impuesto es del producto, no de la cotización ni de la variante —
        // TaxRate.cs (Catalog) deja este cálculo a propósito para acá. Sin TaxRateId, o con una
        // tasa que ya no existe, la variante cotiza con 0% (null hacia Quotations).
        var taxPercentages = await CatalogVariantReads.LoadTaxPercentagesAsync(
            taxRateRepository, tenantId, products, cancellationToken);

        return selected.ToDictionary(
            pair => pair.Variant.Id.Value,
            pair => new QuotationVariantPricingRef(
                pair.Variant.Id.Value,
                pair.Product.Id.Value,
                pair.Product.TenantId,
                CatalogVariantReads.DisplayNameOf(pair.Product, pair.Variant, valueNames),
                pair.Variant.Code,
                CatalogVariantReads.IsSellable(pair.Product, pair.Variant),
                CatalogVariantReads.PricesOf(pair.Variant),
                pair.Variant.ToQuotationRefs(),
                pair.Product.TaxRateId is { } id && taxPercentages.TryGetValue(id, out var percentage)
                    ? percentage
                    : null));
    }
}
