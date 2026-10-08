namespace Modules.Tenancy.Domain;

/// <summary>
/// The two currencies the catalogue can price (`Product.PriceBaseCop` / `PriceBaseUsd`). Declared
/// here and not borrowed from Quotations because Tenancy sits below every business module.
/// </summary>
public static class TenantCurrencies
{
    public const string Cop = "COP";
    public const string Usd = "USD";

    public static string Normalize(string value)
    {
        var normalized = value.Trim().ToUpperInvariant();
        return normalized is Cop or Usd
            ? normalized
            : throw new TenantDomainException(
                "tenancy.settings.default_currency.invalid",
                $"Default currency must be {Cop} or {Usd}.");
    }
}
