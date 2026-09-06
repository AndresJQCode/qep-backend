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
    public static QuotationPriceScaleRef ToQuotationRef(this PriceScale scale) =>
        new(
            scale.FromUnit,
            scale.ToUnit,
            scale.Discount,
            ToRestriction(scale.Restriction),
            scale.Multiple,
            scale.PackagingUnit);

    private static QuotationPriceScaleRestriction ToRestriction(PriceScaleRestriction restriction) =>
        restriction switch
        {
            PriceScaleRestriction.Multiple => QuotationPriceScaleRestriction.Multiple,
            PriceScaleRestriction.PackagingUnit => QuotationPriceScaleRestriction.PackagingUnit,
            _ => throw new ArgumentOutOfRangeException(nameof(restriction))
        };
}
