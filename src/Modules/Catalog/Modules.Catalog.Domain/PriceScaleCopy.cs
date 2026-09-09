namespace Modules.Catalog.Domain;

/// <summary>
/// Las escalas de un producto, tal como quedarían en otro.
///
/// **Puro y estático, igual que <see cref="ProductPriceChangeDetector"/>**: no muta ninguno de
/// los dos productos, no lee la base y no depende del reloj. El caso de uso lo llama para armar
/// el precio del destino *antes* de aplicarlo, que es justo lo que el detector de histórico
/// necesita para comparar contra lo que había.
///
/// Lo que se copia es la **forma** del tramo —rango, descuento, restricción, múltiplo o empaque,
/// agrupación—, nunca el precio final. Ese se recalcula contra el precio base **del destino**,
/// y es la corrección que motivó este tipo: <see cref="PriceScale.Create"/> valida el final
/// contra el precio base del producto dueño, así que arrastrar el final del origen hacía fallar
/// la copia con <c>catalog.product.price_scale.final_mismatch_usd</c> en todo destino cuyo
/// precio base no fuera idéntico al del origen.
///
/// Una moneda sin precio base en el destino queda sin final, porque el dominio prohíbe un final
/// sin su base. Nunca quedan las dos sin él: todo producto tiene precio base en al menos una
/// moneda.
/// </summary>
public static class PriceScaleCopy
{
    /// <returns>
    /// El precio completo que le queda al destino: sus propios precios base —esta operación no
    /// los toca— y las escalas del origen recalculadas. Devuelve el <see cref="ProductPricing"/>
    /// entero, y no sólo las escalas, porque es lo que consume
    /// <see cref="ProductPriceChangeDetector.Detect"/>.
    /// </returns>
    public static ProductPricing ToPricingFor(Product source, Product target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        return new ProductPricing
        {
            BaseUsd = target.PriceBaseUsd,
            BaseCop = target.PriceBaseCop,
            // Ordenadas por rango, igual que el histórico: sin esto el orden de las filas nuevas
            // lo decide el orden en que EF materializó las del origen, y dos copias del mismo
            // origen podrían quedar distintas sin que nadie toque nada.
            Scales = source.PriceScales
                .OrderBy(scale => scale.FromUnit)
                .ThenBy(scale => scale.ToUnit)
                .Select(scale => ToInputFor(scale, target))
                .ToArray()
        };
    }

    private static PriceScaleInput ToInputFor(PriceScale scale, Product target) => new(
        scale.FromUnit,
        scale.ToUnit,
        scale.Discount,
        scale.Restriction,
        scale.Multiple,
        scale.PackagingUnit,
        PriceScale.FinalFor(target.PriceBaseUsd, scale.Discount),
        PriceScale.FinalFor(target.PriceBaseCop, scale.Discount),
        scale.AllowGrouping);
}
