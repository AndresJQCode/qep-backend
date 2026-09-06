using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Resuelve un producto contra el catálogo **del tenant de la cotización** y calcula el
/// descuento por escala para la cantidad pedida (US-3/US-4). Mismo criterio que
/// <c>ProductImageResolver</c> en Catalog: sin FK real que respalde la referencia, esta
/// comprobación es la única red.
///
/// El precio sale de la moneda de la cotización: la que fija su cuenta de cobro. No hay
/// conversión en ningún punto — si el producto no tiene precio cargado en esa moneda, no se
/// puede cotizar ahí, y eso se dice como error y no como una cifra aproximada.
/// </summary>
internal static class QuotationProductPricingResolver
{
    public static async Task<QuotationPricedProduct> ResolveAsync(
        IQuotationProductPricingLookup lookup,
        Guid tenantId,
        Guid productId,
        decimal quantity,
        QuotationCurrency currency,
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

        return new QuotationPricedProduct(product.Name, PriceFor(product, quantity, currency));
    }

    /// <summary>
    /// Los precios de varias líneas de una vez, en la moneda dada. Lo usa el cambio de moneda:
    /// todas las líneas se revalorizan juntas o no se revaloriza ninguna.
    ///
    /// A diferencia del alta, acá **no** se rechaza el producto inactivo: la línea ya está en la
    /// cotización desde antes, y darlo de baja después no es motivo para bloquear un cambio de
    /// cuenta. Lo que sí bloquea es que no exista precio en la moneda nueva — ese importe no se
    /// puede inventar.
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, QuotationItemPricing>> ResolveManyAsync(
        IQuotationProductPricingLookup lookup,
        Guid tenantId,
        IReadOnlyCollection<(Guid ProductId, decimal Quantity)> lines,
        QuotationCurrency currency,
        CancellationToken cancellationToken)
    {
        if (lines.Count == 0)
        {
            return new Dictionary<Guid, QuotationItemPricing>();
        }

        var products = await lookup.FindManyAsync(
            tenantId,
            lines.Select(line => line.ProductId).Distinct().ToArray(),
            cancellationToken);

        var priced = new Dictionary<Guid, QuotationItemPricing>();
        foreach (var (productId, quantity) in lines)
        {
            if (!products.TryGetValue(productId, out var product) || product.TenantId != tenantId)
            {
                throw new QuotationsDomainException(
                    "quotation.item.product_not_found",
                    $"Product '{productId}' was not found in this tenant.");
            }

            priced[productId] = PriceFor(product, quantity, currency);
        }

        return priced;
    }

    private static QuotationItemPricing PriceFor(
        QuotationProductPricingRef product, decimal quantity, QuotationCurrency currency)
    {
        var unitPrice = currency == QuotationCurrency.Usd
            ? product.UnitPriceUsd
            : product.UnitPriceCop;

        if (unitPrice is not { } price)
        {
            throw new QuotationsDomainException(
                "quotation.item.product_price_unavailable",
                $"The product does not have a price in {currency.ToCode()}.");
        }

        var scale = QuotationDiscountResolver.Resolve(product.Scales, quantity);

        // La escala que cubre la cantidad no sólo le pone el descuento: también decide qué
        // cantidades son pedibles. Va acá y no en los handlers para que valga igual al agregar y
        // al editar una línea — los dos pasan por este método.
        if (scale is not null)
        {
            QuotationScaleRestrictionRule.EnsureSatisfied(scale, quantity);
        }

        return new QuotationItemPricing(price, scale?.Discount ?? 0m, product.TaxPercentage ?? 0);
    }
}

/// <summary>El producto ya valorizado, con su nombre: el nombre no entra en el cálculo, lo usa
/// el resumen del historial ("Agregó Bebidas x3") y sale de la misma consulta que el precio.
/// </summary>
internal sealed record QuotationPricedProduct(string Name, QuotationItemPricing Pricing);
