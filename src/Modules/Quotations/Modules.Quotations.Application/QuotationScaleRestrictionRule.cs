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
///
/// **Desde el 2026-10-01 el empaque es un conjunto, y es del producto**: la keratina de 120 ml
/// viene en cajas de 100 y de 150, y vale toda cantidad que se arme con cajas enteras de
/// cualquiera de los dos, mezcladas — 100, 150, 200, 250, 300 … sí; 50 y 120 no. Con un solo
/// empaque es exactamente la regla de antes. Ver <see cref="EvaluatePackaging"/>.
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
            // Desde cero: el empaque se cuenta crudo. Ver el resumen del tipo.
            QuotationPriceScaleRestriction.PackagingUnit => EvaluatePackaging(
                scale.PackagingUnits, quantity),
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
            QuotationPriceScaleRestriction.PackagingUnit => EvaluatePackaging(
                scale.PackagingUnits, quantity),
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

    /// <summary>
    /// Por encima de este empaque menor la cuenta por restos dejaría de ser barata —un arreglo
    /// por resto—, así que se cae a mirar cada empaque por separado. Ningún empaque real se le
    /// acerca; existe para que un dato absurdo cargado en Catalog no reserve gigas de memoria en
    /// cada cotización.
    /// </summary>
    private const int MaxSmallestPackagingUnit = 100_000;

    /// <summary>
    /// Si la cantidad se arma con cajas enteras de los empaques del producto, mezcladas, y si no,
    /// cuánto le falta para la siguiente que sí.
    ///
    /// No recorre las cantidades hasta la pedida —una línea de un millón haría un millón de
    /// pasos—. Trabaja sobre los restos módulo el empaque menor <c>m</c>: para cada resto
    /// calcula la menor cantidad armable que lo tiene (<see cref="LowestByResidue"/>), y como
    /// sumarle <c>m</c> sigue siendo armable, una cantidad lo es si y sólo si no queda por debajo
    /// de la menor de su resto. Cuesta O(empaques × m), sin importar la cantidad.
    ///
    /// Los defensivos son los de <see cref="EvaluateStep"/>: un conjunto vacío o con un empaque
    /// menor o igual a cero contradice lo que Catalog hace cumplir, y la línea no pierde el
    /// descuento por un dato que nadie puede corregir desde la cotización.
    /// </summary>
    private static QuotationScaleRestrictionResult EvaluatePackaging(
        IReadOnlyList<int> packagingUnits, decimal quantity)
    {
        const string code = "quotation.item.quantity_not_packaging_unit";

        if (packagingUnits.Count == 0 || packagingUnits.Any(unit => unit <= 0))
        {
            return QuotationScaleRestrictionResult.Satisfied(quantity);
        }

        if (quantity < 0)
        {
            return new QuotationScaleRestrictionResult(false, code, quantity, 0m);
        }

        // Una cantidad con decimales nunca son cajas enteras: se busca desde el entero siguiente.
        var target = decimal.Ceiling(quantity);
        var smallest = packagingUnits.Min();
        var next = smallest > MaxSmallestPackagingUnit
            ? NextMultipleOfAny(packagingUnits, target)
            : NextRepresentable(LowestByResidue(packagingUnits, smallest), smallest, target);

        return next == quantity
            ? QuotationScaleRestrictionResult.Satisfied(quantity)
            : new QuotationScaleRestrictionResult(false, code, quantity, next - quantity);
    }

    /// <summary>
    /// Para cada resto <c>r</c> módulo <paramref name="smallest"/>, la menor cantidad armable con
    /// los empaques que deja ese resto; <see cref="long.MaxValue"/> si ninguna lo deja (con 100 y
    /// 150 sólo se alcanzan los restos 0 y 50).
    ///
    /// Es el algoritmo "round robin" de Böcker y Lipták: por cada empaque recorre los ciclos que
    /// forma sumarlo módulo <paramref name="smallest"/>, arrancando cada uno desde su mínimo para
    /// que una sola vuelta alcance.
    /// </summary>
    private static long[] LowestByResidue(IReadOnlyList<int> packagingUnits, int smallest)
    {
        var lowest = new long[smallest];
        Array.Fill(lowest, long.MaxValue);
        lowest[0] = 0;

        foreach (var unit in packagingUnits.Distinct())
        {
            if (unit == smallest)
            {
                continue;
            }

            var cycles = GreatestCommonDivisor(unit, smallest);
            var cycleLength = smallest / cycles;
            var stride = unit % smallest;

            for (var start = 0; start < cycles; start++)
            {
                var from = start;
                var residue = start;
                for (var step = 1; step < cycleLength; step++)
                {
                    residue = (residue + stride) % smallest;
                    if (lowest[residue] < lowest[from])
                    {
                        from = residue;
                    }
                }

                var current = lowest[from];
                if (current == long.MaxValue)
                {
                    continue;
                }

                for (var step = 1; step < cycleLength; step++)
                {
                    current += unit;
                    residue = (int)(current % smallest);
                    current = Math.Min(current, lowest[residue]);
                    lowest[residue] = current;
                }
            }
        }

        return lowest;
    }

    // La menor cantidad armable que no queda por debajo de target (entero, >= 0). Por cada resto
    // alcanzable: su mínimo si ya llega a target; si no, el primer valor de ese resto desde target.
    private static decimal NextRepresentable(long[] lowest, int smallest, decimal target)
    {
        var targetResidue = (int)(target % smallest);
        var best = decimal.MaxValue;

        for (var residue = 0; residue < smallest; residue++)
        {
            if (lowest[residue] == long.MaxValue)
            {
                continue;
            }

            var candidate = lowest[residue] >= target
                ? lowest[residue]
                : target + ((residue - targetResidue + smallest) % smallest);
            best = Math.Min(best, candidate);
        }

        return best;
    }

    // El atajo para un empaque menor absurdo (ver MaxSmallestPackagingUnit): mira cada empaque
    // por separado, sin combinarlos. Puede negar una mezcla válida, nunca dar por buena una que
    // no lo es — y con un solo empaque es la regla exacta.
    private static decimal NextMultipleOfAny(IReadOnlyList<int> packagingUnits, decimal target) =>
        packagingUnits.Min(unit => target + ((unit - (target % unit)) % unit));

    private static int GreatestCommonDivisor(int left, int right)
    {
        while (right != 0)
        {
            (left, right) = (right, left % right);
        }

        return left;
    }

    // Catalog exige un paso > 0 al crear la escala. Si una fila lo desmiente, la línea no se
    // castiga con un dato que nadie puede corregir desde la cotización — y sobre todo no se
    // divide por cero.
    //
    // <paramref name="floor"/> es el origen del conteo: <c>FromUnit</c> en Evaluate, 0 en
    // EvaluateFromZero. Sólo lo usa el múltiplo desde el 2026-10-01: el empaque pasó a ser un
    // conjunto y tiene su propia cuenta (EvaluatePackaging), que siempre arranca en cero.
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
