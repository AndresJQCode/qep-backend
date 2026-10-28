using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>
/// The minimum purchase that enables any scale discount (spec 2026-10-08, D7): <see cref="MinimumUnits"/>
/// OR the quotation total reaching the minimum of its currency. Tenant configuration owned by
/// Quotations. A currency without an entry in <see cref="MinimumTotals"/> only passes by units —
/// fail-closed, never a converted threshold.
/// </summary>
public sealed record QuotationSettings(int MinimumUnits, IReadOnlyDictionary<string, decimal> MinimumTotals)
{
    /// <summary>
    /// What a tenant without a stored row gets: the constants this rule had before it was
    /// configurable (decision of the owner, 2026-09-21). AddQuotationSettings seeds the same
    /// values for every existing tenant, so production behaviour does not move on deploy.
    /// </summary>
    public static QuotationSettings Default { get; } = new(
        6, new Dictionary<string, decimal> { ["COP"] = 500_000m, ["USD"] = 200m });

    public decimal? MinimumTotalFor(string currency) =>
        MinimumTotals.TryGetValue(currency, out var total) ? total : null;

    /// <summary>The domain-code net behind <c>UpdateQuotationSettingsValidator</c>, which gives
    /// the field. Normalises the codes against the catalogue.</summary>
    public static QuotationSettings Create(int minimumUnits, IReadOnlyDictionary<string, decimal> minimumTotals)
    {
        if (minimumUnits < 1)
        {
            throw new QuotationsDomainException(
                "quotations.settings.minimum_units.invalid",
                "The minimum units must be at least 1.");
        }

        return new QuotationSettings(
            minimumUnits,
            minimumTotals.ToDictionary(
                entry => Currencies.Normalize(entry.Key), entry => entry.Value, StringComparer.Ordinal));
    }
}
