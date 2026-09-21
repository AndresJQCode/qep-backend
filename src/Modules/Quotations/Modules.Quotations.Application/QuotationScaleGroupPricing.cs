namespace Modules.Quotations.Application;

/// <summary>Una línea tal como queda después de la mutación, con la cantidad que el recálculo
/// debe considerar.</summary>
public sealed record QuotationPricingLine(Guid ItemId, Guid ProductId, decimal Quantity);

/// <param name="Grouped">Si la cantidad evaluada fue la del grupo y no la de la línea. Viaja a
/// la respuesta: "te faltan 2 unidades" significa cosas distintas según de quién sean.</param>
public sealed record QuotationLinePricing(
    Guid ItemId,
    decimal DiscountPercentage,
    QuotationPriceScaleRef? Scale,
    QuotationScaleRestrictionResult? Restriction,
    bool Grouped);

/// <summary>
/// Resuelve el descuento de **todas** las líneas de una cotización a la vez, porque desde que
/// existe la agrupación el descuento de una línea depende de las otras.
///
/// La agrupación hace **dos** cosas, y conviene no confundirlas:
///
/// 1. **Cumplir el múltiplo** de un tramo que la línea ya alcanzó sola. Es lo original: 10 + 8 +
///    12 = 30 es múltiplo de 3 aunque 10 y 8 no lo sean.
/// 2. **Alcanzar un tramo** al que la línea no llega sola (2026-09-21). Dos productos con 3
///    unidades cada uno suman 6 y los dos toman el tramo 6-48, aunque solos caigan en 1-5.
///
/// El punto 2 revierte lo que decía este mismo comentario hasta esa fecha —"la suma nunca decide
/// en qué escala cae una línea"— y por eso cambió el orden de resolución: los totales de grupo se
/// calculan **antes** que nada, desde los tramos que el **catálogo** del producto declara
/// agrupables, y no desde el tramo que cada línea alcanzó por su cuenta. Sin eso hay huevo y
/// gallina: para agrupar haría falta el tramo, y para elegir el tramo haría falta el grupo.
///
/// La clave del grupo sigue siendo <c>FromUnit</c> + <c>ToUnit</c> + <c>Multiple</c>. El descuento
/// queda **fuera**: es parámetro de cada línea, así que dos productos con la misma escala agrupan
/// aunque descuenten distinto, y cada uno conserva el suyo. La agrupación decide **si** la escala
/// aplica y puede subir de tramo a la línea, pero nunca la baja — ver <see cref="Upgrade"/>.
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
    /// Cuánto suma cada tramo agrupable, sobre **todas** las líneas de la cotización cuyo producto
    /// lo declare — se mira el catálogo del producto, no el tramo en el que la línea cayó sola.
    ///
    /// Sólo suma la línea que **cabe bajo el techo** del tramo. Una que ya lo superó no lo
    /// necesita —alcanzó sola uno igual o mejor— y sumarla sacaría al grupo entero del rango:
    /// 3 + 3 + 100 = 106 se pasa de 48 y les costaría el descuento a las dos líneas chicas que el
    /// grupo venía a rescatar. Decisión del owner, 2026-09-21.
    ///
    /// Las que ya cumplen y sí caben **sí** suman: el requisito cuenta 6 + 10 = 16.
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
                if (!IsGroupable(scale) || line.Quantity > scale.ToUnit)
                {
                    continue;
                }

                var key = GroupKey(scale);
                totals[key] = totals.GetValueOrDefault(key) + line.Quantity;
            }
        }

        return totals;
    }

    private static IReadOnlyCollection<QuotationPriceScaleRef> ScalesOf(
        IReadOnlyDictionary<Guid, IReadOnlyCollection<QuotationPriceScaleRef>> scalesByProduct,
        Guid productId) =>
        scalesByProduct.TryGetValue(productId, out var scales) ? scales : [];

    /// <summary>
    /// Primero lo que la línea consigue por su cuenta —incluido el rescate del múltiplo, que es el
    /// comportamiento de siempre—, y recién después se mira si algún tramo agrupable le da más.
    /// Sólo lo reemplaza si el descuento es **estrictamente** mayor: con empate gana lo propio, y
    /// así una línea que ya cumplía sola no queda marcada como agrupada.
    /// </summary>
    private static QuotationLinePricing ToPricing(
        QuotationPricingLine line,
        IReadOnlyCollection<QuotationPriceScaleRef> scales,
        Dictionary<(int, int, int), decimal> groupTotals)
    {
        var own = OwnPricing(
            line, QuotationDiscountResolver.Resolve(scales, line.Quantity), groupTotals);

        return Upgrade(line, scales, groupTotals, own) ?? own;
    }

    private static QuotationLinePricing OwnPricing(
        QuotationPricingLine line,
        QuotationPriceScaleRef? scale,
        Dictionary<(int, int, int), decimal> groupTotals)
    {
        if (scale is null)
        {
            return new QuotationLinePricing(line.ItemId, 0m, null, null, false);
        }

        // La agrupación sólo rescata a las que no cumplen solas: una línea que ya cumple
        // conserva su escala aunque el total del grupo falle. No le cambia el veredicto a
        // ninguna otra —con múltiplo puro, una línea que cumple es congruente con 0 módulo el
        // paso, así que entra o sale de la suma sin mover el resto—, y evita que el
        // incumplimiento de una línea se cobre sobre la de al lado.
        var individual = QuotationScaleRestrictionRule.Evaluate(scale, line.Quantity);
        if (individual.IsSatisfied || !IsGroupable(scale))
        {
            return new QuotationLinePricing(
                line.ItemId,
                individual.IsSatisfied ? scale.Discount : 0m,
                scale,
                individual,
                false);
        }

        // El total sí lleva las cantidades de todas las líneas del grupo, incluidas las que
        // cumplen: es el número que la pantalla muestra para explicar el faltante, y el
        // requisito lo cuenta así (6 + 10 = 16).
        var grouped = QuotationScaleRestrictionRule.Evaluate(scale, groupTotals[GroupKey(scale)]);

        return new QuotationLinePricing(
            line.ItemId,
            grouped.IsSatisfied ? scale.Discount : 0m,
            scale,
            grouped,
            true);
    }

    /// <summary>
    /// El mejor tramo agrupable que la línea alcanza **gracias al grupo** y no sola: tiene que
    /// caber bajo su techo, el total del grupo tiene que caer dentro del rango —que es justamente
    /// lo que significa "alcanzar el tramo"— y ese total tiene que cumplir el múltiplo.
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
            if (!IsGroupable(scale)
                || line.Quantity > scale.ToUnit
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

            var grouped = QuotationScaleRestrictionRule.Evaluate(scale, total);
            if (!grouped.IsSatisfied)
            {
                continue;
            }

            best = new QuotationLinePricing(line.ItemId, scale.Discount, scale, grouped, true);
        }

        return best;
    }

    // El paso > 0 es invariante de Catalog; exigirlo acá evita que una fila que la desmienta
    // arme un grupo que después nadie sabe contra qué comparar.
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
