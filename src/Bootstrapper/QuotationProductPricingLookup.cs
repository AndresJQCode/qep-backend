using Modules.Catalog.Application;
using Modules.Catalog.Domain;
using Modules.Quotations.Application;

namespace Bootstrapper;

/// <summary>
/// Adapta el repositorio de <c>Catalog</c> al puerto que <c>quotations</c> declara. Mismo
/// criterio de aislamiento que <see cref="QuotationCustomerLookup"/>.
///
/// No decide nada: sólo traduce el producto y sus escalas de precio al vocabulario de
/// <c>quotations</c>, con **los dos** precios base que el catálogo guarda. Cuál de los dos aplica
/// —y qué escala, para una cantidad— es de
/// <c>QuotationDiscountResolver</c>/<c>QuotationProductPricingResolver</c>, en Application.
/// </summary>
internal sealed class QuotationProductPricingLookup(
    IProductRepository repository, ITaxRateRepository taxRateRepository)
    : IQuotationProductPricingLookup
{
    public async Task<QuotationProductPricingRef?> FindAsync(
        Guid tenantId, Guid productId, CancellationToken cancellationToken)
    {
        var product = await repository.FindAsync(
            tenantId, new ProductId(productId), cancellationToken);
        if (product is null)
        {
            return null;
        }

        var scales = product.PriceScales
            .Select(scale => scale.ToQuotationRef())
            .ToArray();

        // RN-013: la tasa de impuesto es del producto, no de la cotización — TaxRate.cs (Catalog)
        // deja este cálculo a propósito para acá. Sin TaxRateId, o con una tasa que ya no existe,
        // el producto cotiza con 0%.
        int? taxPercentage = null;
        if (product.TaxRateId is { } taxRateId)
        {
            var taxRate = await taxRateRepository.FindAsync(tenantId, taxRateId, cancellationToken);
            taxPercentage = taxRate?.Percentage;
        }

        return new QuotationProductPricingRef(
            product.Id.Value, product.TenantId, product.Name, product.IsActive,
            product.PriceBaseCop, product.PriceBaseUsd, scales, taxPercentage);
    }

    // Una consulta para todos los productos y otra para todas las tasas: revalorizar una
    // cotizacion de diez lineas cuesta dos lecturas, no veinte.
    public async Task<IReadOnlyDictionary<Guid, QuotationProductPricingRef>> FindManyAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> productIds,
        CancellationToken cancellationToken)
    {
        if (productIds.Count == 0)
        {
            return new Dictionary<Guid, QuotationProductPricingRef>();
        }

        var products = await repository.ListByIdsAsync(
            tenantId,
            productIds.Distinct().Select(id => new ProductId(id)).ToArray(),
            cancellationToken);

        var taxRateIds = products
            .Select(product => product.TaxRateId)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToArray();
        var taxPercentages = new Dictionary<TaxRateId, int>();
        foreach (var taxRateId in taxRateIds)
        {
            var taxRate = await taxRateRepository.FindAsync(tenantId, taxRateId, cancellationToken);
            if (taxRate is not null)
            {
                taxPercentages[taxRateId] = taxRate.Percentage;
            }
        }

        return products.ToDictionary(
            product => product.Id.Value,
            product => new QuotationProductPricingRef(
                product.Id.Value,
                product.TenantId,
                product.Name,
                product.IsActive,
                product.PriceBaseCop,
                product.PriceBaseUsd,
                product.PriceScales
                    .Select(scale => scale.ToQuotationRef())
                    .ToArray(),
                product.TaxRateId is { } id && taxPercentages.TryGetValue(id, out var percentage)
                    ? percentage
                    : null));
    }
}
