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

        // Cotización detal: ninguna línea recibe descuento, así que no hay nada que resolver y el
        // corte va acá arriba, junto al de la cotización vacía — consultar el catálogo sería una
        // lectura por cada guardado del editor sin ningún efecto sobre el resultado.
        //
        // Se baja 0% explícito en vez de dejar la línea como está: el descuento con el que el
        // handler la agregó es provisional (ver el resumen de arriba), y prender el detal sobre
        // una cotización que ya tenía descuentos resueltos tiene que borrarlos.
        //
        // Origen Own y no uno propio: 0% con un origen de escala le haría decir a la pantalla
        // "descuento de escala aplicado" sobre nada. Mismo criterio y mismo shape que el barrido
        // de compra mínima de más abajo. Va por ApplyGroupDiscounts, que no es una edición y por
        // eso no toca Version ni Updated* — ver Quotation.ApplyGroupDiscounts.
        if (quotation.IsRetail)
        {
            quotation.ApplyGroupDiscounts(
                quotation.Items.ToDictionary(
                    item => item.Id,
                    _ => new QuotationItemDiscount(0m, QuotationDiscountOrigin.Own)),
                occurredAt);

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

        var lines = quotation.Items
            .Select(item => new QuotationPricingLine(
                item.Id.Value, item.ProductId, item.Quantity))
            .ToArray();

        var discounts = Apply(
            quotation, lines, scalesByProduct, quotation.GlobalScaleFloor, occurredAt);

        // La compuerta se evalúa sobre el total que dejaron esos descuentos, y una sola vez: si no
        // alcanza, se quitan todos y se termina. Ver QuotationMinimumPurchase para por qué no se
        // vuelve a mirar.
        if (QuotationMinimumPurchase.IsSatisfiedBy(quotation))
        {
            return;
        }

        // El piso global no puede dejar a la cotización **peor** que no haberlo elegido, que es lo
        // que el spec promete: es un piso, no un techo. Y puede: la compuerta mide sobre el total
        // ya descontado, así que un descuento más grande puede tirar el total por debajo del
        // mínimo y disparar el barrido. Cinco unidades a 110.000 con su 5% propio dan 522.500 y
        // pasan; con el 10% del piso global dan 495.000 y no, con lo que la línea termina en 0% y
        // el cliente pagando 550.000 — más caro que antes de pedir el descuento.
        //
        // Antes de barrer se prueba sin el piso. Es una segunda resolución en memoria sobre los
        // productos que ya están cargados: ni una consulta más.
        if (quotation.GlobalScaleFloor is not null)
        {
            discounts = Apply(quotation, lines, scalesByProduct, globalFloor: null, occurredAt);

            if (QuotationMinimumPurchase.IsSatisfiedBy(quotation))
            {
                return;
            }
        }

        // Vuelven todas a Own y no sólo a 0%: una línea que conservara GlobalFloor con cero por
        // ciento le haría decir a la pantalla "descuento global aplicado" sobre nada.
        quotation.ApplyGroupDiscounts(
            discounts.ToDictionary(
                discount => discount.Key,
                _ => new QuotationItemDiscount(0m, QuotationDiscountOrigin.Own)),
            occurredAt);
    }
    /// <summary>
    /// Resuelve y baja los descuentos al agregado, con o sin piso global, y devuelve lo que
    /// aplicó. Separado porque el recálculo lo hace hasta dos veces: la segunda para comprobar
    /// que el piso global no empeoró el resultado.
    /// </summary>
    private static Dictionary<QuotationItemId, QuotationItemDiscount> Apply(
        Quotation quotation,
        IReadOnlyCollection<QuotationPricingLine> lines,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<QuotationPriceScaleRef>> scalesByProduct,
        int? globalFloor,
        DateTimeOffset occurredAt)
    {
        var discounts = QuotationScaleGroupPricing
            .Resolve(lines, scalesByProduct, globalFloor)
            .ToDictionary(
                line => new QuotationItemId(line.ItemId),
                line => new QuotationItemDiscount(line.DiscountPercentage, line.Origin));

        quotation.ApplyGroupDiscounts(discounts, occurredAt);

        return discounts;
    }

}
