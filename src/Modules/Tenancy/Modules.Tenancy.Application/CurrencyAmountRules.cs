using FluentValidation.Results;

namespace Modules.Tenancy.Application;

/// <summary>
/// Per-row validation of an amount-per-currency map (product prices, minimum totals). One failure
/// per bad row, keyed <c>{path}.{CODE}</c>, so the form marks that row (spec: "Unknown code ->
/// tenancy.currency.unsupported mapped to pricing.prices.&lt;CODE&gt;"). Shared here because Catalog
/// and Quotations cannot reference each other, and both maps must fail the same way.
/// </summary>
public static class CurrencyAmountRules
{
    public static IEnumerable<ValidationFailure> Check(
        IReadOnlyDictionary<string, decimal> amounts, string path)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (code, amount) in amounts)
        {
            var key = code?.Trim().ToUpperInvariant() ?? string.Empty;
            var property = $"{path}.{key}";
            if (!Currencies.IsSupported(key))
            {
                yield return new ValidationFailure(property, $"Currency '{code}' is not supported.")
                {
                    ErrorCode = Currencies.UnsupportedCode
                };
                continue;
            }

            if (!seen.Add(key))
            {
                yield return new ValidationFailure(property, $"Currency '{key}' appears more than once.");
            }

            if (amount < 0m)
            {
                yield return new ValidationFailure(property, "The amount cannot be negative.");
            }
        }
    }
}
