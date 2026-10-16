using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

public sealed class DocumentTotalsTests
{
    [Fact]
    public void SumsLinesAndAppliesRetentionOnTheSubtotal()
    {
        var totals = DocumentTotals.Compute(
            [new LineAmountsView(Subtotal: 100_000m, DiscountAmount: 5_000m, TaxAmount: 19_000m),
             new LineAmountsView(Subtotal: 50_000m, DiscountAmount: 0m, TaxAmount: 9_500m)],
            appliesVatSurplus: false,
            appliesRetention: true);

        Assert.Equal(150_000m, totals.Subtotal);
        Assert.Equal(5_000m, totals.DiscountAmount);
        Assert.Equal(28_500m, totals.TaxAmount);
        Assert.Equal(19m, totals.TaxPercentage);
        Assert.Equal(178_500m, totals.Total);
        Assert.Equal(3_750m, totals.RetentionAmount); // 2,5 % del subtotal
        Assert.Equal(174_750m, totals.NetTotal);
    }

    [Fact]
    public void VatSurplusZeroesTheTaxAndTheRate()
    {
        var totals = DocumentTotals.Compute(
            [new LineAmountsView(100_000m, 0m, 19_000m)], appliesVatSurplus: true, appliesRetention: false);

        Assert.Equal((0m, 0m, 100_000m, 100_000m), (totals.TaxAmount, totals.TaxPercentage, totals.Total, totals.NetTotal));
    }

    [Fact]
    public void NoLinesGivesZeroEverywhere()
    {
        var totals = DocumentTotals.Compute([], appliesVatSurplus: false, appliesRetention: true);

        Assert.Equal(DocumentTotals.Zero, totals);
    }
}
