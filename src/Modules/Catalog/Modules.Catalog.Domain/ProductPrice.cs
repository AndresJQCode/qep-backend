namespace Modules.Catalog.Domain;

/// <summary>
/// The base price of a variant in one currency, VAT included (spec 2026-10-08, D3; of the product
/// until the variants of 2026-10-20). Owned by a <see cref="ProductVariant"/> inside the
/// <see cref="Product"/> aggregate: no repository, and only the aggregate creates or changes it.
/// The code arrives normalised by the application (<c>Currencies.Normalize</c>): this assembly
/// cannot see the catalogue.
/// </summary>
public sealed class ProductPrice
{
    // EF Core materialises through here.
    private ProductPrice() => Currency = string.Empty;

    internal ProductPrice(string currency, decimal amount)
    {
        Currency = currency;
        Amount = amount;
    }

    public string Currency { get; private set; }

    public decimal Amount { get; private set; }

    internal void ChangeAmount(decimal amount) => Amount = amount;
}
