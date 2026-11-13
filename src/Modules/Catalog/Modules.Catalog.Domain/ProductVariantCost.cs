namespace Modules.Catalog.Domain;

/// <summary>
/// The manual unit cost of a variant in one currency (spec 2026-11-10). Owned by a
/// <see cref="ProductVariant"/> inside the <see cref="Product"/> aggregate, like
/// <see cref="ProductPrice"/>: no repository, and only the aggregate creates or changes it. The
/// code arrives normalised by the application; the amount is always positive (a cost that is not
/// known is the absence of the row, never a zero).
/// </summary>
public sealed class ProductVariantCost
{
    // EF Core materialises through here.
    private ProductVariantCost() => Currency = string.Empty;

    internal ProductVariantCost(string currency, decimal amount)
    {
        Currency = currency;
        Amount = amount;
    }

    public string Currency { get; private set; }

    public decimal Amount { get; private set; }

    internal void ChangeAmount(decimal amount) => Amount = amount;
}
