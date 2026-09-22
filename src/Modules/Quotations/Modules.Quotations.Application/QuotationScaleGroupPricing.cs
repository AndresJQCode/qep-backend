namespace Modules.Quotations.Application;

/// <summary>Una línea tal como queda después de la mutación, con la cantidad que el recálculo
/// debe considerar.</summary>
public sealed record QuotationPricingLine(Guid ItemId, Guid ProductId, decimal Quantity);

/// <param name="Grouped">Si el tramo se lo dio la suma del grupo y no su propia cantidad. Viaja a
/// la respuesta: "te lo dieron entre todas" y "te lo ganaste sola" no son lo mismo en pantalla.</param>
public sealed record QuotationLinePricing(
    Guid ItemId,
    decimal DiscountPercentage,
    QuotationPriceScaleRef? Scale,
    QuotationScaleRestrictionResult? Restriction,
    bool Grouped);

/// <summary>
/// Resuelve el descuento de **todas** las líneas de una cotización a la vez, porque desde que
/// existe la agrupación el tramo de una línea depende de las otras.
///
/// <b>El múltiplo es por línea.</b> Una línea de 5 unidades en un tramo de a 3 no cumple, y no la
/// arregla nadie: no recibe descuento y **tampoco suma al grupo**. Corregido por el owner el
/// 2026-09-21, y es un cambio de criterio respecto de cómo nació esto — hasta esa fecha el
/// múltiplo se validaba sobre la suma, y un grupo de 10 + 8 + 12 = 30 le daba el descuento a las
/// tres aunque 10 y 8 no fueran múltiplos de 3. Ya no: ahora sólo lo recibe la de 12.
///
/// <b>El grupo sirve para alcanzar el rango, y para nada más.</b> Cinco líneas de 3 unidades no
/// llegan solas al tramo 6-48, y sumadas dan 15, que sí cae adentro: las cinco se llevan su
/// descuento. La que no cumple el múltiplo ni siquiera entra en esa cuenta, así que no puede
/// arrastrar al resto — que era justamente el defecto reportado: una línea de 5 dejaba a cinco
/// líneas de 3 sin descuento porque 3×5 + 5 = 20 no es múltiplo de 3.
///
/// <b>El total sólo se compara contra el rango</b>, nunca contra el múltiplo. Desde que el
/// múltiplo se cuenta desde <c>FromUnit</c> (2026-09-21) ya no es cierto que una suma de
/// miembros válidos sea válida —tres líneas de 5 en un tramo 5-48 de a 3 suman 15, y 15 − 5 = 10
/// no es múltiplo de 3—, y así se decidió que quede: el piso se exige sobre la suma y el
/// múltiplo por miembro. Revalidar el total volvería frágil la construcción de a poco, que es
/// para lo que existe el grupo.
///
/// Los miembros del grupo son por definición líneas que **no llegan al piso** del tramo, y ahí
/// el múltiplo se cuenta crudo: por debajo de <c>FromUnit</c> no hay contra qué anclar un
/// offset. Ver <see cref="Qualifies"/>.
///
/// La clave del grupo es <c>FromUnit</c> + <c>ToUnit</c> + <c>Multiple</c>, tomada de los tramos
/// que el **catálogo** del producto declara agrupables y no del tramo que la línea alcanzó sola:
/// al revés habría huevo y gallina, porque para elegir el tramo haría falta el grupo. El descuento
/// queda fuera de la clave, así que dos productos con la misma escala agrupan aunque descuenten
/// distinto, y cada uno conserva el suyo.
///
/// Nunca lanza. El 422 de <c>PackagingUnit</c> vive en <c>QuotationProductPricingResolver</c>,
/// sobre la línea que el comando toca — ver <c>QuotationScaleRestrictionRule</c>.
/// </summary>
internal static class QuotationScaleGroupPricing
{
    public static IReadOnlyList<QuotationLinePricing> Resolve(
        IReadOnlyCollection<QuotationPricingLine> lines,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<QuotationPriceScaleRef>> scalesByProduct)
    {
        var groupTotals = GroupTotals(lines, scalesByProduct);

        return lines
            .Select(line => ToPricing(line, ScalesOf(scalesByProduct, line.ProductId), groupTotals))
            .ToArray();
    }

    /// <summary>
    /// Cuánto suma cada tramo agrupable, contando **sólo las líneas que califican solas**.
    ///
    /// Una línea que no cumple el múltiplo del tramo no entra: si entrara, su cantidad movería el
    /// total de las demás sin que ella pueda recibir nada a cambio. Y una que ya llega al piso
    /// tampoco, porque no necesita al grupo —el tramo ya la cubre— y sumarla sacaría del rango a
    /// las que sí lo necesitan.
    /// </summary>
    private static Dictionary<(int, int, int), decimal> GroupTotals(
        IReadOnlyCollection<QuotationPricingLine> lines,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<QuotationPriceScaleRef>> scalesByProduct)
    {
        var totals = new Dictionary<(int, int, int), decimal>();

        foreach (var line in lines)
        {
            foreach (var scale in ScalesOf(scalesByProduct, line.ProductId))
            {
                if (!Qualifies(scale, line.Quantity))
                {
                    continue;
                }

                var key = GroupKey(scale);
                totals[key] = totals.GetValueOrDefault(key) + line.Quantity;
            }
        }

        return totals;
    }

    /// <summary>
    /// Si esta línea puede sumar a este tramo y beneficiarse de él: el tramo agrupa, la cantidad
    /// **no alcanza el piso sola** —es lo único que el grupo puede darle— y cumple su múltiplo
    /// crudo por sí sola.
    ///
    /// El piso reemplazó al techo que había acá hasta el 2026-09-21, y lo subsume: una cantidad
    /// por debajo de <c>FromUnit</c> está por debajo de <c>ToUnit</c>. Sin él, una línea que el
    /// tramo ya cubre armaba un grupo de una sola línea cuyo total era ella misma, caía en el
    /// rango y cobraba el descuento que el múltiplo desde <c>FromUnit</c> le niega —marcada
    /// además como agrupada, sin nadie con quien agrupar—. Por esa puerta el conteo crudo volvía
    /// a gobernar toda escala agrupable y el cambio de criterio quedaba sin efecto.
    ///
    /// Adentro del rango manda <c>QuotationScaleRestrictionRule</c> y nada más: la línea cumple
    /// el múltiplo desde el piso o no descuenta.
    /// </summary>
    private static bool Qualifies(QuotationPriceScaleRef scale, decimal quantity) =>
        IsGroupable(scale)
        && quantity < scale.FromUnit
        && quantity % scale.Multiple!.Value == 0;

    private static IReadOnlyCollection<QuotationPriceScaleRef> ScalesOf(
        IReadOnlyDictionary<Guid, IReadOnlyCollection<QuotationPriceScaleRef>> scalesByProduct,
        Guid productId) =>
        scalesByProduct.TryGetValue(productId, out var scales) ? scales : [];

    /// <summary>
    /// Primero lo que la línea consigue por su cuenta, y recién después si algún tramo agrupable le
    /// da más. Sólo lo reemplaza con un descuento **estrictamente** mayor: con empate gana lo
    /// propio, y así una línea que ya calificaba sola no queda marcada como agrupada.
    /// </summary>
    private static QuotationLinePricing ToPricing(
        QuotationPricingLine line,
        IReadOnlyCollection<QuotationPriceScaleRef> scales,
        Dictionary<(int, int, int), decimal> groupTotals)
    {
        var own = OwnPricing(line, QuotationDiscountResolver.Resolve(scales, line.Quantity));

        return Upgrade(line, scales, groupTotals, own) ?? own;
    }

    /// <summary>
    /// Lo que la línea vale sola: el tramo que cubre su cantidad, y su descuento sólo si cumple la
    /// restricción de ese tramo. Sin rescate de ningún tipo — el múltiplo es por línea.
    /// </summary>
    private static QuotationLinePricing OwnPricing(
        QuotationPricingLine line, QuotationPriceScaleRef? scale)
    {
        if (scale is null)
        {
            return new QuotationLinePricing(line.ItemId, 0m, null, null, false);
        }

        var individual = QuotationScaleRestrictionRule.Evaluate(scale, line.Quantity);

        return new QuotationLinePricing(
            line.ItemId,
            individual.IsSatisfied ? scale.Discount : 0m,
            scale,
            individual,
            false);
    }

    /// <summary>
    /// El mejor tramo agrupable que la línea alcanza **gracias al grupo** y no sola: tiene que
    /// calificar (<see cref="Qualifies"/>) y el total del grupo tiene que caer dentro del rango,
    /// que es lo que significa "alcanzar el tramo".
    ///
    /// El múltiplo no se revalida sobre el total: lo cumple cada miembro, y una suma de múltiplos
    /// del mismo paso también lo es.
    ///
    /// Devuelve <c>null</c> cuando ninguno mejora lo que la línea ya tenía.
    /// </summary>
    private static QuotationLinePricing? Upgrade(
        QuotationPricingLine line,
        IReadOnlyCollection<QuotationPriceScaleRef> scales,
        Dictionary<(int, int, int), decimal> groupTotals,
        QuotationLinePricing own)
    {
        QuotationLinePricing? best = null;

        foreach (var scale in scales)
        {
            if (!Qualifies(scale, line.Quantity)
                || scale.Discount <= own.DiscountPercentage
                || (best is not null && scale.Discount <= best.DiscountPercentage))
            {
                continue;
            }

            var total = groupTotals.GetValueOrDefault(GroupKey(scale));
            if (total < scale.FromUnit || total > scale.ToUnit)
            {
                continue;
            }

            // La cantidad evaluada es la del grupo: es el número que explica en pantalla por qué
            // una línea de 3 unidades se llevó el tramo que empieza en 6.
            best = new QuotationLinePricing(
                line.ItemId,
                scale.Discount,
                scale,
                QuotationScaleRestrictionResult.Satisfied(total),
                true);
        }

        return best;
    }

    // El paso > 0 es invariante de Catalog; exigirlo acá evita dividir por cero al validar el
    // múltiplo, y que una fila que lo desmienta arme un grupo contra el que nadie sabe comparar.
    private static bool IsGroupable(QuotationPriceScaleRef? scale) =>
        scale is
        {
            Restriction: QuotationPriceScaleRestriction.Multiple,
            AllowGrouping: true,
            Multiple: > 0
        };

    private static (int, int, int) GroupKey(QuotationPriceScaleRef scale) =>
        (scale.FromUnit, scale.ToUnit, scale.Multiple!.Value);
}
