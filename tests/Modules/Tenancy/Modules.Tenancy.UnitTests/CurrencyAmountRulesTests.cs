using Modules.Tenancy.Application;

namespace Modules.Tenancy.UnitTests;

public sealed class CurrencyAmountRulesTests
{
    [Fact]
    public void AValidMapHasNoFailures() =>
        Assert.Empty(CurrencyAmountRules.Check(
            new Dictionary<string, decimal> { ["COP"] = 45_000m, ["eur"] = 0m }, "Pricing.Prices"));

    // The key carries the code so the form marks that row and no other.
    [Fact]
    public void AnUnknownCodeFailsOnItsOwnRowWithTheCatalogueCode()
    {
        var failure = Assert.Single(CurrencyAmountRules.Check(
            new Dictionary<string, decimal> { ["COP"] = 1m, ["xyz"] = 2m }, "Pricing.Prices"));

        Assert.Equal("Pricing.Prices.XYZ", failure.PropertyName);
        Assert.Equal("tenancy.currency.unsupported", failure.ErrorCode);
    }

    [Fact]
    public void ANegativeAmountFailsOnItsRow()
    {
        var failure = Assert.Single(CurrencyAmountRules.Check(
            new Dictionary<string, decimal> { ["USD"] = -1m }, "MinimumTotals"));

        Assert.Equal("MinimumTotals.USD", failure.PropertyName);
    }

    [Fact]
    public void TheSameCurrencyTwiceFailsOnTheSecondRow()
    {
        var failure = Assert.Single(CurrencyAmountRules.Check(
            new Dictionary<string, decimal> { ["USD"] = 1m, ["usd"] = 2m }, "Pricing.Prices"));

        Assert.Equal("Pricing.Prices.USD", failure.PropertyName);
    }
}
