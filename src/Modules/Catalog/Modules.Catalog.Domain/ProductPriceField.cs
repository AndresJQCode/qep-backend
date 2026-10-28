namespace Modules.Catalog.Domain;

/// <summary>
/// What changed in a <see cref="ProductPriceChange"/> row: a base price (one row per currency,
/// <see cref="ProductPriceChange.Currency"/> says which) or the discount of a scale. Stored as
/// text; the Reporting mirror <c>PriceChangeField</c> follows.
/// </summary>
public enum ProductPriceField
{
    PriceBase,
    ScaleDiscount
}
