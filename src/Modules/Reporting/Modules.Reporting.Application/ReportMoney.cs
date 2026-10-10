using Modules.Tenancy.Application;

namespace Modules.Reporting.Application;

/// <summary>One amount in one currency. Every money figure of a report is a list of these (spec
/// 2026-10-08, Reports): there is no conversion, so COP and USD are never added together.</summary>
public sealed record ReportMoneyDto(string Currency, decimal Amount);

public static class ReportMoney
{
    /// <summary>Groups by currency, catalogue order. Only currencies with rows appear: an empty
    /// list means "no money", never a zero in some currency.</summary>
    public static IReadOnlyList<ReportMoneyDto> From(IEnumerable<(string Currency, decimal Amount)> amounts) =>
        amounts
            .GroupBy(amount => amount.Currency.Trim(), StringComparer.Ordinal)
            .Select(group => new ReportMoneyDto(group.Key, group.Sum(amount => amount.Amount)))
            .OrderBy(money => Currencies.OrderOf(money.Currency))
            .ThenBy(money => money.Currency, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// The "Otros" row of a ranking, per currency: the total minus the named entries. By
    /// subtraction against the already computed total, never a third query — same rule as
    /// before (ReportRankFolding), now applied currency by currency. A currency the named entries
    /// cover entirely is left out.
    /// </summary>
    public static IReadOnlyList<ReportMoneyDto> Remainder(
        IReadOnlyList<ReportMoneyDto> total, IEnumerable<IReadOnlyList<ReportMoneyDto>> named)
    {
        var covered = named
            .SelectMany(entry => entry)
            .GroupBy(money => money.Currency, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Sum(money => money.Amount), StringComparer.Ordinal);

        return From(total.Select(money => (money.Currency, money.Amount - covered.GetValueOrDefault(money.Currency))))
            .Where(money => money.Amount != 0m)
            .ToArray();
    }
}
