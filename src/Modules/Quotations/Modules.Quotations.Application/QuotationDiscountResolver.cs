namespace Modules.Quotations.Application;

/// <summary>
/// Resuelve qué escala de precios del producto cubre una cantidad dada (US-4). Decisiones ya
/// confirmadas con el equipo: el descuento nunca es editable a mano, y una cantidad que no cae
/// en ninguna escala definida da 0% — no bloquea la línea ni usa la escala más cercana.
///
/// Devuelve la escala entera y no sólo su descuento porque quien llama también necesita su
/// restricción de cantidad — ver <c>QuotationScaleRestrictionRule</c>.
/// </summary>
internal static class QuotationDiscountResolver
{
    public static QuotationPriceScaleRef? Resolve(
        IReadOnlyCollection<QuotationPriceScaleRef> scales, decimal quantity)
    {
        foreach (var scale in scales)
        {
            if (quantity >= scale.FromUnit && quantity <= scale.ToUnit)
            {
                return scale;
            }
        }

        return null;
    }
}
