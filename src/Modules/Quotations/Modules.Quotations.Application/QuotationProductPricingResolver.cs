using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Resuelve un producto contra el catálogo **del tenant de la cotización** y calcula el
/// descuento por escala para la cantidad pedida (US-3/US-4). Mismo criterio que
/// <c>ProductImageResolver</c> en Catalog: sin FK real que respalde la referencia, esta
/// comprobación es la única red.
/// </summary>
internal static class QuotationProductPricingResolver
{
    public static async Task<(decimal UnitPrice, decimal DiscountPercentage, int TaxPercentage)> ResolveAsync(
        IQuotationProductPricingLookup lookup,
        Guid tenantId,
        Guid productId,
        decimal quantity,
        CancellationToken cancellationToken)
    {
        var product = await lookup.FindAsync(tenantId, productId, cancellationToken);

        // Mismo código para "no existe" y "es de otro tenant" — la frontera de tenant no se
        // distingue desde afuera.
        if (product is null || product.TenantId != tenantId)
        {
            throw new QuotationsDomainException(
                "quotation.item.product_not_found",
                $"Product '{productId}' was not found in this tenant.");
        }

        if (!product.IsActive)
        {
            throw new QuotationsDomainException(
                "quotation.item.product_inactive",
                "An inactive product cannot be added to a quotation.");
        }

        if (product.UnitPriceCop is not { } unitPrice)
        {
            throw new QuotationsDomainException(
                "quotation.item.product_price_unavailable",
                "The product does not have a price in COP.");
        }

        var discountPercentage = QuotationDiscountResolver.Resolve(product.Scales, quantity);
        return (unitPrice, discountPercentage, product.TaxPercentage ?? 0);
    }
}
