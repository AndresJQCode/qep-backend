using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Resuelve una variante contra el catálogo **del tenant de la cotización** y calcula el
/// descuento por escala para la cantidad pedida (US-3/US-4). Por variante desde el spec 2026-10-20
/// §4: precio, escalas y empaques son de la variante; los códigos de error siguen diciendo
/// <c>product_*</c> porque son contrato con el frontend. Mismo criterio que
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
        Guid variantId,
        decimal quantity,
        string currency,
        bool isRetail,
        CancellationToken cancellationToken)
    {
        var product = await lookup.FindVariantAsync(tenantId, variantId, cancellationToken);

        // Mismo código para "no existe" y "es de otro tenant" — la frontera de tenant no se
        // distingue desde afuera.
        if (product is null || product.TenantId != tenantId)
        {
            throw new QuotationsDomainException(
                "quotation.item.product_not_found",
                $"Variant '{variantId}' was not found in this tenant.");
        }

        if (!product.IsActive)
        {
            throw new QuotationsDomainException(
                "quotation.item.product_inactive",
                "An inactive product cannot be added to a quotation.");
        }

        return new QuotationPricedProduct(
            product.ProductId, product.Code, product.Name, PriceFor(product, quantity, currency, isRetail));
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
        IReadOnlyCollection<(Guid VariantId, decimal Quantity)> lines,
        string currency,
        bool isRetail,
        CancellationToken cancellationToken)
    {
        if (lines.Count == 0)
        {
            return new Dictionary<Guid, QuotationItemPricing>();
        }

        var products = await lookup.FindManyAsync(
            tenantId,
            lines.Select(line => line.VariantId).Distinct().ToArray(),
            cancellationToken);

        var priced = new Dictionary<Guid, QuotationItemPricing>();
        foreach (var (variantId, quantity) in lines)
        {
            if (!products.TryGetValue(variantId, out var product) || product.TenantId != tenantId)
            {
                throw new QuotationsDomainException(
                    "quotation.item.product_not_found",
                    $"Variant '{variantId}' was not found in this tenant.");
            }

            priced[variantId] = PriceFor(product, quantity, currency, isRetail);
        }

        return priced;
    }

    private static QuotationItemPricing PriceFor(
        QuotationVariantPricingRef product,
        decimal quantity,
        string currency,
        bool isRetail)
    {
        // The quotation currency's own price, never another one: there is no conversion.
        if (!product.Prices.TryGetValue(currency, out var price))
        {
            throw new QuotationsDomainException(
                "quotation.item.product_price_unavailable",
                $"The product does not have a price in {currency}.");
        }

        // Cotización detal (decisión del owner, 2026-09-23): "el sistema debe omitir cualquier
        // escala de precio y no validar restricciones". Las escalas no se miran, así que tampoco
        // se exige que estén completas: un producto a medio configurar en el catálogo se cotiza
        // igual, a precio de lista. El precio en la moneda sí se sigue exigiendo — no es una
        // escala, es el importe, y no se puede inventar.
        if (isRetail)
        {
            return new QuotationItemPricing(price, 0m, product.TaxPercentage ?? 0);
        }

        // Antes que el descuento, y también desde ResolveManyAsync: cambiar la moneda es volver a
        // fijar el precio de cada línea, y ese precio depende de la restricción que falta.
        QuotationScaleRestrictionRule.EnsureScalesComplete(product.Scales);

        var scale = QuotationDiscountResolver.Resolve(product.Scales, quantity);

        // Ninguna restricción bloquea: si no se cumple, la escala no aplica y la línea va sin
        // descuento. `PackagingUnit` lanzaba un 422 acá hasta el 2026-09-22. Todavía sin
        // agrupar — eso lo agrega QuotationScaleGroupPricing, que recalcula todas juntas.
        var discount = scale is not null
            && QuotationScaleRestrictionRule.Evaluate(scale, quantity).IsSatisfied
                ? scale.Discount
                : 0m;

        return new QuotationItemPricing(price, discount, product.TaxPercentage ?? 0);
    }
}

/// <summary>La variante ya valorizada, con su producto, su código y su nombre visible: ninguno entra
/// en el cálculo. El nombre lo usa el resumen del historial ("Agregó Camiseta – M x3"), el producto
/// lo guarda la línea junto a la variante (la petición sólo trae <c>variantId</c>) y el código lo
/// usa el pedido para tomar la foto; los tres salen de la misma consulta que el precio.
/// </summary>
internal sealed record QuotationPricedProduct(
    Guid ProductId, string Code, string Name, QuotationItemPricing Pricing);
