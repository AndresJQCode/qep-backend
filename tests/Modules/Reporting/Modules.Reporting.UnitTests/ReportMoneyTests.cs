using Modules.Reporting.Application;

namespace Modules.Reporting.UnitTests;

/// <summary>Review Focus 5: a report never adds COP to USD. Every money figure is a list with
/// one amount per currency that has rows, in catalogue order.</summary>
public sealed class ReportMoneyTests
{
    [Fact]
    public void FromGroupsByCurrencyAndNeverAddsAcrossThem()
    {
        var totals = ReportMoney.From([("USD", 10m), ("COP", 100_000m), ("USD", 5.5m)]);

        Assert.Equal([new ReportMoneyDto("COP", 100_000m), new ReportMoneyDto("USD", 15.5m)], totals);
    }

    // Catalogue order (COP, USD, EUR), not alphabetical and not first-seen.
    [Fact]
    public void FromListsCurrenciesInCatalogueOrder()
    {
        var totals = ReportMoney.From([("EUR", 3m), ("USD", 2m), ("COP", 1m)]);

        Assert.Equal(["COP", "USD", "EUR"], totals.Select(money => money.Currency));
    }

    [Fact]
    public void FromWithoutRowsIsEmptyNotZero() =>
        Assert.Empty(ReportMoney.From([]));

    // The "Otros" row: what the named entries do not cover, currency by currency.
    [Fact]
    public void RemainderSubtractsPerCurrencyAndDropsWhatIsFullyCovered()
    {
        var remainder = ReportMoney.Remainder(
            [new ReportMoneyDto("COP", 900m), new ReportMoneyDto("EUR", 40m)],
            [[new ReportMoneyDto("COP", 500m)], [new ReportMoneyDto("COP", 100m), new ReportMoneyDto("EUR", 40m)]]);

        Assert.Equal([new ReportMoneyDto("COP", 300m)], remainder);
    }
}
