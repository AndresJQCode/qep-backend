using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Vuelve a resolver el descuento de **todas** las líneas de una cotización contra el catálogo y
/// se lo baja al agregado.
///
/// Es la pasada global que la agrupación de escalas necesitaba y que no existía: hasta el
/// 2026-09-21 cada handler valorizaba su línea por separado con
/// <see cref="QuotationProductPricingResolver"/>, así que <c>AllowGrouping</c> estaba escrito,
/// probado y sin efecto en runtime — nadie llamaba a <see cref="QuotationScaleGroupPricing"/>.
///
/// Va **después** de la mutación y **antes** de <c>SaveChangesAsync</c>, en el mismo handler:
/// agregar, quitar o cambiar la cantidad de una línea cambia el descuento de las otras, y el
/// mínimo de compra se mide sobre el total resultante. El descuento que el handler le pasó a
/// <c>AddItem</c> es provisional; el que vale es el que sale de acá.
///
/// No es una edición y no toca la versión del agregado — ver
/// <see cref="Quotation.ApplyGroupDiscounts"/>.
/// </summary>
internal static class QuotationPricingRecalculation
{
    public static async Task ApplyAsync(
        IQuotationProductPricingLookup lookup,
        Guid tenantId,
        Quotation quotation,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        if (quotation.Items.Count == 0)
        {
            return;
        }

        // Una sola consulta al catálogo para toda la cotización: una por línea convertiría un
        // cambio de cantidad en veinte lecturas, mismo criterio que ResolveManyAsync.
        var products = await lookup.FindManyAsync(
            tenantId,
            quotation.Items.Select(item => item.ProductId).Distinct().ToArray(),
            cancellationToken);

        // Un producto que ya no está —borrado o movido de tenant después de cotizarlo— se queda
        // sin escalas y por lo tanto sin descuento. No se lanza: la línea ya está en la
        // cotización, y un recálculo disparado por otra línea no es el momento de romper.
        var scalesByProduct = quotation.Items
            .Select(item => item.ProductId)
            .Distinct()
            .ToDictionary(
                productId => productId,
                productId => products.TryGetValue(productId, out var product)
                    ? product.Scales
                    : []);

        var resolved = QuotationScaleGroupPricing.Resolve(
            quotation.Items
                .Select(item => new QuotationPricingLine(
                    item.Id.Value, item.ProductId, item.Quantity))
                .ToArray(),
            scalesByProduct,
            quotation.GlobalScaleFloor);

        var discounts = resolved.ToDictionary(
            line => new QuotationItemId(line.ItemId),
            line => new QuotationItemDiscount(line.DiscountPercentage, line.Origin));

        quotation.ApplyGroupDiscounts(discounts, occurredAt);

        // La compuerta se evalúa sobre el total que dejaron esos descuentos, y una sola vez: si no
        // alcanza, se quitan todos y se termina. Ver QuotationMinimumPurchase para por qué no se
        // vuelve a mirar.
        if (QuotationMinimumPurchase.IsSatisfiedBy(quotation))
        {
            return;
        }

        // Vuelven todas a Own y no sólo a 0%: una línea que conservara GlobalFloor con cero por
        // ciento le haría decir a la pantalla "descuento global aplicado" sobre nada.
        quotation.ApplyGroupDiscounts(
            discounts.ToDictionary(
                discount => discount.Key,
                _ => new QuotationItemDiscount(0m, QuotationDiscountOrigin.Own)),
            occurredAt);
    }
}
