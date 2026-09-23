namespace Modules.Quotations.Application;

/// <summary>
/// Por qué el piso global no le dio su descuento a una línea.
///
/// Existe porque el asesor elige "Desde 1000", el porcentaje de la línea no se mueve, y la
/// pantalla no le decía nada: en el catálogo real los tramos de 100 en adelante exigen paquetes
/// completos (de 13 a 150 unidades), así que una línea de 6 casi nunca los cumple, y eso se
/// reportó como defecto (2026-09-23). La regla es correcta; lo que faltaba era decirla.
///
/// Usa la misma regla que decide el descuento (<see cref="QuotationScaleRestrictionRule.EvaluateFromZero"/>)
/// y el mismo criterio para elegir el tramo que <c>QuotationScaleGroupPricing.GlobalPricing</c>:
/// de mayor a menor descuento, sólo los que mejoran. Si se escribiera otra regla, el motivo
/// podría contradecir al porcentaje.
///
/// Devuelve <c>null</c> — nada que explicar — cuando no hay piso, cuando la línea lo tomó, cuando
/// su propio descuento ya era igual o mejor, y cuando cumple la restricción pero igual no lo tiene:
/// eso último es la compuerta de compra mínima, que tiene su aviso propio a nivel cotización.
/// </summary>
internal static class QuotationGlobalScaleFloorMiss
{
    public static GlobalScaleFloorMissResponse? Explain(
        decimal quantity,
        decimal discountPercentage,
        string discountOrigin,
        IReadOnlyCollection<QuotationPriceScaleRef> scales,
        int? globalScaleFloor)
    {
        if (globalScaleFloor is not { } floor || discountOrigin == "GlobalFloor")
        {
            return null;
        }

        var atFloor = scales.Where(scale => scale.FromUnit == floor).ToArray();
        if (atFloor.Length == 0)
        {
            return new GlobalScaleFloorMissResponse("no_tier", null);
        }

        // El tramo que el piso le habría dado: el mejor de los que mejoran lo que ya tiene.
        var candidate = atFloor
            .Where(scale => scale.Discount > discountPercentage)
            .OrderByDescending(scale => scale.Discount)
            .FirstOrDefault();
        if (candidate is null)
        {
            return null;
        }

        if (QuotationScaleRestrictionRule.EvaluateFromZero(candidate, quantity).IsSatisfied)
        {
            return null;
        }

        return candidate.Restriction switch
        {
            QuotationPriceScaleRestriction.PackagingUnit =>
                new GlobalScaleFloorMissResponse("packaging_unit", candidate.PackagingUnit),
            QuotationPriceScaleRestriction.Multiple =>
                new GlobalScaleFloorMissResponse("multiple", candidate.Multiple),
            // Una escala incompleta (la que deja la copia de escalas) nunca aplica, pero no hay
            // un paso que decirle al asesor: el problema está en el catálogo, no en la cantidad.
            _ => null
        };
    }
}
