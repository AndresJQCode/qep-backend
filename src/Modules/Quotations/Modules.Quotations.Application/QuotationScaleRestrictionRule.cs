using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <param name="EvaluatedQuantity">La cantidad contra la que se evaluó: la de la línea, o la
/// suma del grupo cuando la escala agrupa. Viaja a la respuesta porque un total que la pantalla
/// no puede reconstruir sola es lo único que explica un precio sin descuento.</param>
/// <param name="Shortfall">Cuántas unidades faltan para el siguiente múltiplo. 0 cuando cumple.</param>
public sealed record QuotationScaleRestrictionResult(
    bool IsSatisfied,
    string? Code,
    decimal EvaluatedQuantity,
    decimal Shortfall)
{
    public static QuotationScaleRestrictionResult Satisfied(decimal quantity) =>
        new(true, null, quantity, 0m);
}

/// <summary>
/// Decide si la escala que cubre una cantidad aplica sobre ella (CAT-09 + US-4).
///
/// **Dos modelos de falla, a propósito.** <c>Multiple</c> no bloquea: si no se cumple, la escala
/// no aplica y la línea va con descuento 0 y precio base — lo mismo que ya le pasa a una
/// cantidad que no cae en ninguna escala. Es lo único que hace construible un grupo de a poco:
/// con 422 por línea, un total válido como 10+8+12 no tiene ningún camino de estados
/// intermedios que lo alcance. <c>PackagingUnit</c>, en cambio, conserva intacto su 422 — el
/// requisito exige compatibilidad total con su comportamiento actual.
///
/// **El múltiplo se cuenta sobre la cantidad cruda**, no desde <c>FromUnit</c>. Revierte el
/// criterio de <c>5a76b07</c>, que lo heredaba del CRM: en una escala 5-48 de a 3, 8 unidades
/// era válida (8 − 5 = 3) y ya no lo es. Fue decisión explícita del developer el 2026-09-06.
/// </summary>
internal static class QuotationScaleRestrictionRule
{
    public static QuotationScaleRestrictionResult Evaluate(
        QuotationPriceScaleRef scale, decimal quantity) =>
        scale.Restriction switch
        {
            QuotationPriceScaleRestriction.Multiple => EvaluateStep(
                scale.Multiple, quantity, "quotation.item.quantity_not_multiple"),
            QuotationPriceScaleRestriction.PackagingUnit => EvaluateStep(
                scale.PackagingUnit, quantity, "quotation.item.quantity_not_packaging_unit"),
            _ => QuotationScaleRestrictionResult.Satisfied(quantity)
        };

    /// <summary>
    /// El 422 de la unidad de empaque, sobre la línea que el comando toca. No lo llama el
    /// recalculador: si una línea vieja incumpliera el empaque —sólo posible si la escala cambió
    /// en el catálogo después de agregarla—, lanzar desde ahí haría que quitar una línea sana
    /// fallara con el error de otra, y ese error no lo puede corregir nadie desde la cotización.
    /// </summary>
    public static void EnsurePackagingUnit(QuotationPriceScaleRef scale, decimal quantity)
    {
        if (scale.Restriction != QuotationPriceScaleRestriction.PackagingUnit)
        {
            return;
        }

        var result = Evaluate(scale, quantity);
        if (result.IsSatisfied)
        {
            return;
        }

        throw new QuotationsDomainException(
            result.Code!,
            $"The quantity must be a whole number of packages of {scale.PackagingUnit} units " +
            $"while it falls in the {scale.FromUnit}-{scale.ToUnit} price scale.");
    }

    // Catalog exige un paso > 0 al crear la escala. Si una fila lo desmiente, la línea no se
    // castiga con un dato que nadie puede corregir desde la cotización — y sobre todo no se
    // divide por cero.
    private static QuotationScaleRestrictionResult EvaluateStep(
        int? step, decimal quantity, string code)
    {
        if (step is not { } value || value <= 0)
        {
            return QuotationScaleRestrictionResult.Satisfied(quantity);
        }

        var remainder = quantity % value;
        return remainder == 0
            ? QuotationScaleRestrictionResult.Satisfied(quantity)
            : new QuotationScaleRestrictionResult(false, code, quantity, value - remainder);
    }
}
