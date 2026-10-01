using Modules.Catalog.Domain;
using Modules.Quotations.Application;

namespace Bootstrapper;

/// <summary>
/// El único lugar donde una <see cref="PriceScale"/> de Catalog se vuelve el
/// <see cref="QuotationPriceScaleRef"/> que Quotations declara. Lo comparten los dos
/// adaptadores —<see cref="QuotationProductPricingLookup"/> y
/// <see cref="QuotationProductLookup"/>— para que la traducción de
/// <see cref="PriceScaleRestriction"/> no viva duplicada.
/// </summary>
internal static class QuotationPriceScaleMapping
{
    /// <param name="productPackagingUnits">Los empaques del producto dueño
    /// (<see cref="Product.PackagingUnits"/>). Se copian sólo en la escala
    /// <see cref="PriceScaleRestriction.PackagingUnit"/>: en las demás no significan nada, y
    /// mandarlos igual invitaría a evaluarlos donde no corresponde.</param>
    public static QuotationPriceScaleRef ToQuotationRef(
        this PriceScale scale, IReadOnlyList<int> productPackagingUnits) =>
        new(
            scale.FromUnit,
            scale.ToUnit,
            scale.Discount,
            ToRestriction(scale.Restriction),
            scale.Multiple,
            scale.Restriction == PriceScaleRestriction.PackagingUnit
                ? productPackagingUnits.ToArray()
                : [],
            scale.AllowGrouping);

    // Null pasa como null: la escala incompleta tiene que llegar a Quotations tal cual para que
    // allá se bloquee el producto, no traducirse a un caso que parezca configurado.
    private static QuotationPriceScaleRestriction? ToRestriction(PriceScaleRestriction? restriction) =>
        restriction switch
        {
            null => null,
            PriceScaleRestriction.Multiple => QuotationPriceScaleRestriction.Multiple,
            PriceScaleRestriction.PackagingUnit => QuotationPriceScaleRestriction.PackagingUnit,
            _ => throw new ArgumentOutOfRangeException(nameof(restriction))
        };
}
