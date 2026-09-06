using Modules.Catalog.Application;
using Modules.Catalog.Domain;
using Modules.Quotations.Application;

namespace Bootstrapper;

/// <summary>
/// Adapta el repositorio de <c>Catalog</c> al puerto que <c>quotations</c> declara. Mismo
/// criterio de aislamiento que <see cref="QuotationCustomerLookup"/>.
///
/// No decide nada: sólo traduce el producto y sus escalas de precio al vocabulario de
/// <c>quotations</c>, en COP — el módulo de cotizaciones trabaja en una sola moneda (US-5). La
/// resolución de qué escala aplica para una cantidad es de
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
            product.Id.Value, product.TenantId, product.IsActive, product.PriceBaseCop, scales,
            taxPercentage);
    }
}
