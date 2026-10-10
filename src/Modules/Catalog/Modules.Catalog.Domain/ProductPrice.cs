namespace Modules.Catalog.Domain;

/// <summary>
/// The base price of a product in one currency, VAT included (spec 2026-10-08, D3). Part of the
/// <see cref="Product"/> aggregate: no repository, and only <c>Product</c> creates or changes it.
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
