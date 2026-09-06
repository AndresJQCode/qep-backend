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

        var scale = QuotationDiscountResolver.Resolve(product.Scales, quantity);

        // La escala que cubre la cantidad no sólo le pone el descuento: también decide qué
        // cantidades son pedibles. Va acá y no en los handlers para que valga igual al agregar y
        // al editar una línea — los dos pasan por este método.
        if (scale is not null)
        {
            QuotationScaleRestrictionRule.EnsureSatisfied(scale, quantity);
        }

        return (unitPrice, scale?.Discount ?? 0m, product.TaxPercentage ?? 0);
    }
}
