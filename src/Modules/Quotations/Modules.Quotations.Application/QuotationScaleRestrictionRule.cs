using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <param name="EvaluatedQuantity">La cantidad contra la que se evaluó: la de la línea, o la
/// suma del grupo cuando la escala agrupa. Viaja a la respuesta porque un total que la pantalla
/// no puede reconstruir sola es lo único que explica un precio sin descuento.</param>
/// <param name="Shortfall">Cuántas unidades faltan para el siguiente valor que la restricción
/// acepta. 0 cuando cumple. Con <c>Multiple</c> se cuenta desde <c>FromUnit</c>, así que en una
/// escala 5-48 de a 3 a 7 unidades le falta 1 para llegar a 8, no 2 para llegar a 9.</param>
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
/// **Ninguna restricción bloquea la línea.** Si no se cumple, la escala no aplica y la línea va
/// con descuento 0 y precio base — lo mismo que ya le pasa a una cantidad que no cae en ninguna
/// escala. La cantidad se guarda igual; lo único que pierde es el descuento de ese tramo.
///
/// <c>Multiple</c> es así desde el 2026-09-06, porque es lo único que hace construible un grupo
/// de a poco: con 422 por línea, un total válido como 10+8+12 no tiene ningún camino de estados
/// intermedios que lo alcance. <c>PackagingUnit</c> conservaba un 422 por compatibilidad, y el
/// developer lo quitó el 2026-09-22: una regla de escala decide descuento, no si la línea se
/// puede guardar. El código de restricción no desapareció — viaja en la respuesta para que la
/// pantalla pueda decir por qué esa cantidad no descuenta.
///
/// **El múltiplo se cuenta desde <c>FromUnit</c>**, no sobre la cantidad cruda: en una escala
/// 5-48 de a 3 las cantidades válidas son 5, 8, 11 … 47, y el piso del tramo siempre cumple.
/// Restituye el criterio de <c>5a76b07</c>, heredado del CRM, que el 2026-09-06 se había
/// cambiado al conteo crudo. El developer lo volvió a fijar el 2026-09-21: una escala que
/// arranca en 50 y no descuenta con 50 unidades no tiene explicación para el vendedor.
///
/// Los dos criterios son **disjuntos** siempre que <c>FromUnit</c> no sea múltiplo del paso —en
/// 50-98 de a 6 antes descontaban 54, 60 … 96 y ahora 50, 56 … 98, sin una sola cantidad en
/// común— e **idénticos** cuando sí lo es, que es el caso de la escala 6-48 de a 3 del catálogo
/// sembrado. De ahí que el alcance real del cambio dependa de qué pares
/// <c>FromUnit</c>/<c>Multiple</c> tenga cargado cada tenant.
///
/// **<c>PackagingUnit</c> sigue contando crudo**, y no por omisión: un paquete de 12 son 12
/// unidades enteras empiece donde empiece el tramo, así que correrlo al piso daría por bueno un
/// sobrante. Es el motivo por el que <c>EvaluateStep</c> recibe el piso en vez de leerlo de la
/// escala.
/// </summary>
internal static class QuotationScaleRestrictionRule
{
    /// <summary>
    /// El producto tiene al menos una escala sin restricción —la que deja la copia de escalas en
    /// Catalog— y no se cotiza hasta que alguien la complete.
    /// </summary>
    public const string IncompleteScalesCode = "quotation.item.product_price_scales_incomplete";

    /// <summary>
    /// Una escala incompleta nunca se cumple, y no por azar del <c>switch</c>: antes el
    /// <c>_ =></c> la daba por satisfecha y regalaba su descuento. El bloqueo del producto vive en
    /// <see cref="EnsureScalesComplete"/>; esto es la red para el recalculador, que nunca lanza.
    /// </summary>
    public static QuotationScaleRestrictionResult Evaluate(
        QuotationPriceScaleRef scale, decimal quantity) =>
        scale.Restriction switch
        {
            QuotationPriceScaleRestriction.Multiple => EvaluateStep(
                scale.Multiple, quantity, scale.FromUnit,
                "quotation.item.quantity_not_multiple"),
            // Piso 0: el empaque se cuenta crudo. Ver el resumen del tipo.
            QuotationPriceScaleRestriction.PackagingUnit => EvaluateStep(
                scale.PackagingUnit, quantity, 0,
                "quotation.item.quantity_not_packaging_unit"),
            null => new QuotationScaleRestrictionResult(false, IncompleteScalesCode, quantity, 0m),
            _ => throw new ArgumentOutOfRangeException(
                nameof(scale), scale.Restriction, "Unknown price scale restriction.")
        };

    /// <summary>
    /// La misma restricción, contada **desde cero** en vez de desde <c>FromUnit</c>.
    ///
    /// Existe por el piso global: una línea que no alcanza el tramo por su cuenta está por
    /// debajo de <c>FromUnit</c>, y ahí el offset da negativo y <see cref="EvaluateStep"/> la
    /// rechaza siempre. Sin esto, ninguna línea chica podría cobrar nunca el descuento global —
    /// que es exactamente para lo que el global existe.
    ///
    /// No es criterio nuevo: <c>QuotationScaleGroupPricing.Qualifies</c> ya cuenta crudo por el
    /// mismo motivo, y su comentario lo dice — por debajo del piso no hay contra qué anclar un
    /// offset.
    ///
    /// Quien llama decide cuándo usarla: sólo cuando la cantidad está por debajo del piso del
    /// tramo. Por encima manda <see cref="Evaluate"/>, o el global terminaría aflojando una
    /// restricción que la línea ya alcanzaba sola.
    ///
    /// Para <c>PackagingUnit</c> es idéntica a <see cref="Evaluate"/>: el empaque ya contaba
    /// crudo.
    /// </summary>
    public static QuotationScaleRestrictionResult EvaluateFromZero(
        QuotationPriceScaleRef scale, decimal quantity) =>
        scale.Restriction switch
        {
            QuotationPriceScaleRestriction.Multiple => EvaluateStep(
                scale.Multiple, quantity, 0, "quotation.item.quantity_not_multiple"),
            QuotationPriceScaleRestriction.PackagingUnit => EvaluateStep(
                scale.PackagingUnit, quantity, 0,
                "quotation.item.quantity_not_packaging_unit"),
            null => new QuotationScaleRestrictionResult(false, IncompleteScalesCode, quantity, 0m),
            _ => throw new ArgumentOutOfRangeException(
                nameof(scale), scale.Restriction, "Unknown price scale restriction.")
        };

    /// <summary>
    /// El 422 del producto con escalas incompletas. Mira **todas** las escalas y no sólo la que
    /// cubre la cantidad: una línea de 3 unidades que hoy cae en un tramo completo pasaría a otro
    /// incompleto con cambiarle la cantidad, y el producto a medio configurar no se cotiza en
    /// ningún tramo.
    /// </summary>
    public static void EnsureScalesComplete(IEnumerable<QuotationPriceScaleRef> scales)
    {
        if (scales.All(scale => scale.Restriction is not null))
        {
            return;
        }

        throw new QuotationsDomainException(
            IncompleteScalesCode,
            "The product has price scales that are not fully configured. " +
            "Complete them in the catalog before quoting it.");
    }

    // Catalog exige un paso > 0 al crear la escala. Si una fila lo desmiente, la línea no se
    // castiga con un dato que nadie puede corregir desde la cotización — y sobre todo no se
    // divide por cero.
    //
    // <paramref name="floor"/> es el origen del conteo: <c>FromUnit</c> para el múltiplo, 0 para
    // el empaque. Se pasa y no se deduce de la escala porque la diferencia entre las dos
    // restricciones es justamente esa.
    private static QuotationScaleRestrictionResult EvaluateStep(
        int? step, decimal quantity, int floor, string code)
    {
        if (step is not { } value || value <= 0)
        {
            return QuotationScaleRestrictionResult.Satisfied(quantity);
        }

        var offset = quantity - floor;

        // Defensivo: todo llamador pasa la escala que **cubre** la cantidad, así que el piso
        // nunca queda por encima de ella. Si alguna vez dejara de ser así, el resto de un
        // negativo daría un faltante que nadie puede corregir desde la cotización.
        if (offset < 0)
        {
            return new QuotationScaleRestrictionResult(false, code, quantity, 0m);
        }

        var remainder = offset % value;
        return remainder == 0
            ? QuotationScaleRestrictionResult.Satisfied(quantity)
            : new QuotationScaleRestrictionResult(false, code, quantity, value - remainder);
    }
}
