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
/// El orden importa y es el del requisito: primero cada línea elige su escala por su **propia**
/// cantidad —la suma nunca decide en qué escala cae una línea, ni se compara contra
/// <c>ToUnit</c>—, y recién después las que comparten una escala agrupable suman sus cantidades
/// para validar el múltiplo.
///
/// La clave del grupo es <c>FromUnit</c> + <c>ToUnit</c> + <c>Multiple</c>. El descuento queda
/// **fuera**: es parámetro de cada línea, así que dos productos con la misma escala agrupan
/// aunque descuenten distinto, y cada uno conserva el suyo. La agrupación decide **si** la
/// escala aplica, nunca **cuál**.
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
        var resolved = lines
            .Select(line => (
                Line: line,
                Scale: QuotationDiscountResolver.Resolve(
                    scalesByProduct.TryGetValue(line.ProductId, out var scales) ? scales : [],
                    line.Quantity)))
            .ToArray();

        var groupTotals = resolved
            .Where(entry => IsGroupable(entry.Scale))
            .GroupBy(entry => GroupKey(entry.Scale!))
            .ToDictionary(group => group.Key, group => group.Sum(entry => entry.Line.Quantity));

        return resolved.Select(entry => ToPricing(entry.Line, entry.Scale, groupTotals)).ToArray();
    }

    private static QuotationLinePricing ToPricing(
        QuotationPricingLine line,
        QuotationPriceScaleRef? scale,
        Dictionary<(int, int, int), decimal> groupTotals)
    {
        if (scale is null)
        {
            return new QuotationLinePricing(line.ItemId, 0m, null, null, false);
        }

        var grouped = IsGroupable(scale);
        var quantity = grouped ? groupTotals[GroupKey(scale)] : line.Quantity;
        var restriction = QuotationScaleRestrictionRule.Evaluate(scale, quantity);

        return new QuotationLinePricing(
            line.ItemId,
            restriction.IsSatisfied ? scale.Discount : 0m,
            scale,
            restriction,
            grouped);
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
