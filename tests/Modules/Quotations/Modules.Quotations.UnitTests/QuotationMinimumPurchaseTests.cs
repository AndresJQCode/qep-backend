using Modules.Quotations.Application;

namespace Modules.Quotations.UnitTests;

/// <summary>Spec D7: units OR a minimum total in the quotation currency — and a currency without
/// a configured total only passes by units (fail-closed).</summary>
public sealed class QuotationMinimumPurchaseTests
{
    private static readonly QuotationSettings CopOnly = QuotationSettings.Create(
        6, new Dictionary<string, decimal> { ["COP"] = 500_000m });

    [Fact]
    public void SixUnitsMeetTheMinimumWithNothingMissing()
    {
        var minimum = QuotationMinimumPurchase.DescribeFor(6m, 6_000m, "COP", CopOnly);

        Assert.True(minimum.Met);
        Assert.Equal((0m, (decimal?)0m), (minimum.MissingUnits, minimum.MissingTotal));
    }

    [Fact]
    public void BelowBothBranchesReportsWhatIsMissingInTheQuotationCurrency()
    {
        var minimum = QuotationMinimumPurchase.DescribeFor(2m, 20_000m, "COP", CopOnly);

        Assert.False(minimum.Met);
        Assert.Equal("COP", minimum.Currency);
        Assert.Equal(500_000m, minimum.MinimumTotal);
        Assert.Equal(4m, minimum.MissingUnits);
        Assert.Equal(480_000m, minimum.MissingTotal);
    }

    // Review Focus 3. However large the total, EUR has no configured minimum: only units open it.
    [Fact]
    public void ACurrencyWithoutAConfiguredTotalOnlyPassesByUnits()
    {
        var byTotal = QuotationMinimumPurchase.DescribeFor(2m, 9_000_000m, "EUR", CopOnly);
        var byUnits = QuotationMinimumPurchase.DescribeFor(6m, 10m, "EUR", CopOnly);

        Assert.False(byTotal.Met);
        Assert.Null(byTotal.MinimumTotal);
        Assert.Null(byTotal.MissingTotal);
        Assert.Equal(4m, byTotal.MissingUnits);
        Assert.True(byUnits.Met);
    }

    [Fact]
    public void TheDefaultKeepsTodaysConstants()
    {
        Assert.Equal(6, QuotationSettings.Default.MinimumUnits);
        Assert.Equal(
            new Dictionary<string, decimal> { ["COP"] = 500_000m, ["USD"] = 200m },
            QuotationSettings.Default.MinimumTotals);
    }
}
