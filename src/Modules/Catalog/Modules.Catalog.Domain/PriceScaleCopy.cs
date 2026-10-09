namespace Modules.Catalog.Domain;

/// <summary>
/// The scales of one product, as they would end up on another.
///
/// **Pure and static, like <see cref="ProductPriceChangeDetector"/>**: it mutates neither product,
/// reads no database and does not depend on the clock. The use case calls it to build the target's
/// pricing *before* applying it, which is exactly what the history detector needs to compare
/// against what was there.
///
/// What is copied is the **tier** —range and discount—. Neither the restriction nor what hangs
/// from it (multiple, packaging, grouping): how each product is sold is not inherited from another,
/// so the scale arrives incomplete and someone has to finish configuring it on the target before
/// it can be quoted. Product owner decision, 2026-09-17.
/// </summary>
public static class PriceScaleCopy
{
    /// <returns>
    /// The full pricing the target ends up with: its own prices —this operation does not touch
    /// them— and the source's tiers. It returns the whole <see cref="ProductPricing"/>, and not only
    /// the scales, because that is what <see cref="ProductPriceChangeDetector.Detect"/> consumes.
    /// </returns>
    public static ProductPricing ToPricingFor(Product source, Product target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        return new ProductPricing
        {
            // The target's own prices: this operation copies tiers, never prices.
            Prices = target.Prices.ToDictionary(price => price.Currency, price => price.Amount),
            // The target's own as well: packaging is about how each product is sold.
            PackagingUnits = target.PackagingUnits,
            // Ordered by range, like the history: otherwise the order of the new rows depends on
            // the order EF materialised the source's, and two copies of the same source could differ.
            Scales = source.PriceScales
                .OrderBy(scale => scale.FromUnit)
                .ThenBy(scale => scale.ToUnit)
                .Select(ToInput)
                .ToArray()
        };
    }

    // Range and discount only. Finals are derived, so there is nothing left to recompute against
    // the target (the reason this type existed before spec D4).
    private static PriceScaleInput ToInput(PriceScale scale) => new(
        scale.FromUnit, scale.ToUnit, scale.Discount, Restriction: null, Multiple: null, AllowGrouping: false);
}
