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
            FreightAmounts.Zero,
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
            [new LineAmountsView(100_000m, 0m, 19_000m)], FreightAmounts.Zero, appliesVatSurplus: true, appliesRetention: false);

        Assert.Equal((0m, 0m, 100_000m, 100_000m), (totals.TaxAmount, totals.TaxPercentage, totals.Total, totals.NetTotal));
    }

    [Fact]
    public void NoLinesGivesZeroEverywhere()
    {
        var totals = DocumentTotals.Compute([], FreightAmounts.Zero, appliesVatSurplus: false, appliesRetention: true);

        Assert.Equal(DocumentTotals.Zero, totals);
    }

    private static readonly LineAmountsView[] OneLine = [new LineAmountsView(100_000m, 0m, 19_000m)];

    [Fact]
    public void ChargedFreightInCopAddsItsOwnSubtotalAndTax()
    {
        var freight = FreightAmounts.For(FreightChargeMode.Charged, 15_000m, 19, "COP");

        var totals = DocumentTotals.Compute(OneLine, freight, appliesVatSurplus: false, appliesRetention: false);

        Assert.Equal(12_605.04m, totals.FreightSubtotal);
        Assert.Equal(2_394.96m, totals.FreightTaxAmount);
        Assert.Equal(100_000m, totals.Subtotal);
        Assert.Equal(19_000m, totals.TaxAmount);
        Assert.Equal(134_000m, totals.Total);
    }

    [Fact]
    public void FreeAndOnDeliveryFreightAddNothing()
    {
        Assert.Equal(FreightAmounts.Zero, FreightAmounts.For(FreightChargeMode.Free, 0m, 19, "COP"));
        Assert.Equal(FreightAmounts.Zero, FreightAmounts.For(FreightChargeMode.OnDelivery, 20_000m, 19, "COP"));
    }

    [Fact]
    public void ChargedFreightOutsideCopHasNoTax()
    {
        var freight = FreightAmounts.For(FreightChargeMode.Charged, 30m, 19, "USD");

        Assert.Equal(new FreightAmounts(30m, 0m), freight);
    }

    [Fact]
    public void VatSurplusZeroesTheFreightTaxToo()
    {
        var freight = FreightAmounts.For(FreightChargeMode.Charged, 15_000m, 19, "COP");

        var totals = DocumentTotals.Compute(OneLine, freight, appliesVatSurplus: true, appliesRetention: false);

        Assert.Equal(0m, totals.FreightTaxAmount);
        Assert.Equal(totals.Subtotal + totals.FreightSubtotal, totals.Total);
    }

    [Fact]
    public void RetentionIgnoresTheFreight()
    {
        var freight = FreightAmounts.For(FreightChargeMode.Charged, 15_000m, 19, "COP");

        var totals = DocumentTotals.Compute(OneLine, freight, appliesVatSurplus: false, appliesRetention: true);

        Assert.Equal(2_500m, totals.RetentionAmount);
    }

    [Fact]
    public void ProductsTotalLeavesTheFreightOut()
    {
        var freight = FreightAmounts.For(FreightChargeMode.Charged, 15_000m, 19, "COP");

        var totals = DocumentTotals.Compute(OneLine, freight, appliesVatSurplus: false, appliesRetention: false);

        Assert.Equal(119_000m, totals.ProductsTotal);
    }
}
