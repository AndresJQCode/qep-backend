namespace Modules.Quotations.Application;

/// <summary>
/// Resuelve el descuento por escala de precios del producto para una cantidad dada (US-4).
/// Decisiones ya confirmadas con el equipo: el descuento nunca es editable a mano, y una cantidad
/// que no cae en ninguna escala definida da 0% — no bloquea la línea ni usa la escala más cercana.
/// </summary>
internal static class QuotationDiscountResolver
{
    public static decimal Resolve(
        IReadOnlyCollection<QuotationPriceScaleRef> scales, decimal quantity)
    {
        foreach (var scale in scales)
        {
            if (quantity >= scale.FromUnit && quantity <= scale.ToUnit)
            {
                return scale.Discount;
            }
        }

        return 0m;
    }
}
