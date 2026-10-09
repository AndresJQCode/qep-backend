namespace Modules.Catalog.Domain;

/// <summary>
/// What a product POST/PUT sends to set prices and scales. Grouped for the same reason as
/// <see cref="ProductDetails"/>: the invariants that cross these fields need one place to live.
/// </summary>
public sealed record ProductPricing
{
    /// <summary>Base price per ISO 4217 code, VAT included. Keys arrive normalised by the
    /// application; at least one entry is required (<c>catalog.product.price_required</c>).</summary>
    public IReadOnlyDictionary<string, decimal> Prices { get; init; } = new Dictionary<string, decimal>();

    public IReadOnlyCollection<PriceScaleInput> Scales { get; init; } = [];

    /// <summary>
    /// The packaging units the product comes in (e.g. boxes of 100 and 150). They travel here,
    /// next to the scales, and not in <see cref="ProductDetails"/>: a scale restricted to
    /// <see cref="PriceScaleRestriction.PackagingUnit"/> is validated against this set, so both are
    /// validated together in the same POST/PUT. See <see cref="Product.PackagingUnits"/>.
    /// </summary>
    public IReadOnlyCollection<int> PackagingUnits { get; init; } = [];
}

/// <summary>
/// A price scale as the client sends it, without id and without finals: the final per currency is
/// derived from the product price and the discount (<see cref="PriceScale.FinalFor"/>, spec D4).
///
/// The whole set is replaced on every PUT, so <see cref="Product"/> gives each one a new
/// <see cref="PriceScaleId"/>. It carries no packaging unit: since 2026-10-01 packaging belongs to
/// the product (<see cref="ProductPricing.PackagingUnits"/>).
/// </summary>
/// <param name="AllowGrouping">Whether the quantities of several quotation lines that fall in this
/// scale are added up to validate the multiple. Only for
/// <see cref="PriceScaleRestriction.Multiple"/>. Last and defaulted on purpose: existing scales do
/// not group, and existing positional constructions stay untouched.</param>
public sealed record PriceScaleInput(
    int FromUnit,
    int ToUnit,
    decimal Discount,
    PriceScaleRestriction? Restriction,
    int? Multiple,
    bool AllowGrouping = false);
