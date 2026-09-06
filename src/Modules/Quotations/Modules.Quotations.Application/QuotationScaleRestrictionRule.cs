using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Exige lo que la escala de precios que cubre la cantidad pedida configura sobre esa cantidad
/// (CAT-09 + US-4). Se aplica igual al alta y a la edición de una línea, porque los dos pasan
/// por <see cref="QuotationProductPricingResolver"/>.
///
/// Las dos restricciones no se cuentan sobre la misma base, y no es un descuido:
/// <c>Multiple</c> cuenta el paso **desde <see cref="QuotationPriceScaleRef.FromUnit"/>** —una
/// escala 5-48 de a 3 admite 5, 8, 11…, y la cantidad de entrada al rango siempre entra—,
/// mientras que <c>PackagingUnit</c> cuenta empaques enteros sobre la cantidad cruda, porque un
/// empaque no se parte por dónde arranque el rango. Es la misma asimetría que ya tenía el CRM
/// del que salió esta regla.
///
/// Una cantidad que no cae en ninguna escala no llega acá: sigue sin descuento y sin bloqueo
/// (decisión confirmada, ver <see cref="QuotationDiscountResolver"/>).
/// </summary>
internal static class QuotationScaleRestrictionRule
{
    public static void EnsureSatisfied(QuotationPriceScaleRef scale, decimal quantity)
    {
        switch (scale.Restriction)
        {
            case QuotationPriceScaleRestriction.Multiple:
                EnsureMultiple(scale, quantity);
                break;
            case QuotationPriceScaleRestriction.PackagingUnit:
                EnsurePackagingUnit(scale, quantity);
                break;
        }
    }

    private static void EnsureMultiple(QuotationPriceScaleRef scale, decimal quantity)
    {
        // Catalog exige un múltiplo > 0 al crear la escala. Si una fila lo desmiente, la línea
        // no se bloquea con un dato que nadie puede corregir desde la cotización — y sobre todo
        // no se divide por cero.
        if (scale.Multiple is not { } multiple || multiple <= 0)
        {
            return;
        }

        if (quantity == scale.FromUnit || (quantity - scale.FromUnit) % multiple == 0)
        {
            return;
        }

        throw new QuotationsDomainException(
            "quotation.item.quantity_not_multiple",
            $"The quantity must go in steps of {multiple} from {scale.FromUnit} while it falls " +
            $"in the {scale.FromUnit}-{scale.ToUnit} price scale.");
    }

    private static void EnsurePackagingUnit(QuotationPriceScaleRef scale, decimal quantity)
    {
        if (scale.PackagingUnit is not { } packagingUnit || packagingUnit <= 0)
        {
            return;
        }

        if (quantity % packagingUnit == 0)
        {
            return;
        }

        throw new QuotationsDomainException(
            "quotation.item.quantity_not_packaging_unit",
            $"The quantity must be a whole number of packages of {packagingUnit} units while it " +
            $"falls in the {scale.FromUnit}-{scale.ToUnit} price scale.");
    }
}
