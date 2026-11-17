using System.Globalization;
using BuildingBlocks.Domain.Pricing;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

public sealed class DocumentTotalsTests
{
    private static WithholdingRate Rate(
        string type, decimal percentage, decimal? minimumBase = null, decimal? taxUnitValue = null, string country = "CO") =>
        new(Guid.NewGuid(), $"{type} {percentage}", type, country, percentage, minimumBase,
            minimumBase is null ? null : "UVT", taxUnitValue);

    private static readonly WithholdingRate Retefuente = Rate("Retefuente", 2.5m);

    private static decimal Dec(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);

    [Fact]
    public void SumsLinesAndAppliesRetentionOnTheSubtotal()
    {
        var totals = DocumentTotals.Compute(
            [new LineAmountsView(Subtotal: 100_000m, DiscountAmount: 5_000m, TaxAmount: 19_000m),
             new LineAmountsView(Subtotal: 50_000m, DiscountAmount: 0m, TaxAmount: 9_500m)],
            FreightAmounts.Zero,
            appliesVatSurplus: false,
            [Retefuente]);

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
            [new LineAmountsView(100_000m, 0m, 19_000m)], FreightAmounts.Zero, appliesVatSurplus: true, []);

        Assert.Equal((0m, 0m, 100_000m, 100_000m), (totals.TaxAmount, totals.TaxPercentage, totals.Total, totals.NetTotal));
    }

    [Fact]
    public void NoLinesGivesZeroEverywhere()
    {
        var totals = DocumentTotals.Compute([], FreightAmounts.Zero, appliesVatSurplus: false, []);

        Assert.Equal(DocumentTotals.Zero, totals);
        Assert.Empty(DocumentTotals.Zero.Withholdings);
        Assert.Empty(default(DocumentTotals).Withholdings);
    }

    private static readonly LineAmountsView[] OneLine = [new LineAmountsView(100_000m, 0m, 19_000m)];

    [Fact]
    public void ChargedFreightInCopAddsItsOwnSubtotalAndTax()
    {
        var freight = FreightAmounts.For(FreightChargeMode.Charged, 15_000m, 19, "COP", PriceRounding.LegacyFor("COP"));

        var totals = DocumentTotals.Compute(OneLine, freight, appliesVatSurplus: false, []);

        Assert.Equal(12_605.04m, totals.FreightSubtotal);
        Assert.Equal(2_394.96m, totals.FreightTaxAmount);
        Assert.Equal(100_000m, totals.Subtotal);
        Assert.Equal(19_000m, totals.TaxAmount);
        Assert.Equal(134_000m, totals.Total);
    }

    [Fact]
    public void FreeAndOnDeliveryFreightAddNothing()
    {
        Assert.Equal(FreightAmounts.Zero, FreightAmounts.For(FreightChargeMode.Free, 0m, 19, "COP", PriceRounding.LegacyFor("COP")));
        Assert.Equal(FreightAmounts.Zero, FreightAmounts.For(FreightChargeMode.OnDelivery, 20_000m, 19, "COP", PriceRounding.LegacyFor("COP")));
    }

    [Fact]
    public void ChargedFreightOutsideCopHasNoTax()
    {
        var freight = FreightAmounts.For(FreightChargeMode.Charged, 30m, 19, "USD", PriceRounding.LegacyFor("USD"));

        Assert.Equal(new FreightAmounts(30m, 0m), freight);
    }

    [Fact]
    public void VatSurplusZeroesTheFreightTaxToo()
    {
        var freight = FreightAmounts.For(FreightChargeMode.Charged, 15_000m, 19, "COP", PriceRounding.LegacyFor("COP"));

        var totals = DocumentTotals.Compute(OneLine, freight, appliesVatSurplus: true, []);

        Assert.Equal(0m, totals.FreightTaxAmount);
        Assert.Equal(totals.Subtotal + totals.FreightSubtotal, totals.Total);
    }

    // Mismas líneas y envío que la prueba con appliesRetention: true de antes del cambio: la Retefuente
    // al 2,5 % sin base tiene que dar los mismos números que daba la retención fija, sin tocar el envío.
    [Fact]
    public void RegressionRetefuenteTwoAndAHalfGivesTodaysNumbers()
    {
        var freight = FreightAmounts.For(FreightChargeMode.Charged, 15_000m, 19, "COP", PriceRounding.LegacyFor("COP"));

        var totals = DocumentTotals.Compute(OneLine, freight, appliesVatSurplus: false, [Retefuente]);

        Assert.Equal(2_500m, totals.RetentionAmount);
        Assert.Equal(134_000m, totals.Total);
        Assert.Equal(131_500m, totals.NetTotal);
        Assert.Equal([new AppliedWithholding(Retefuente, 100_000m, 2_500m)], totals.Withholdings);
    }

    [Fact]
    public void TwoWithholdingsOnSubtotalWithoutVatNorFreight()
    {
        var freight = FreightAmounts.For(FreightChargeMode.Charged, 20_000m, 19, "COP", PriceRounding.LegacyFor("COP"));
        var reteIca = Rate("ReteICA", 0.966m);

        var totals = DocumentTotals.Compute(
            [new LineAmountsView(1_000_000m, 0m, 190_000m)], freight, appliesVatSurplus: false, [Retefuente, reteIca]);

        Assert.Equal(1_210_000m, totals.Total);
        Assert.Equal(
            [new AppliedWithholding(Retefuente, 1_000_000m, 25_000m), new AppliedWithholding(reteIca, 1_000_000m, 9_660m)],
            totals.Withholdings);
        Assert.Equal(34_660m, totals.RetentionAmount);
        Assert.Equal(totals.Total - 34_660m, totals.NetTotal);
    }

    // Base de 4 UVT con la UVT a 50.000: umbral 200.000, inclusivo.
    [Theory]
    [InlineData("199999.99", false)]
    [InlineData("200000", true)]
    [InlineData("200000.01", true)]
    public void MinimumBaseInUvt(string subtotal, bool applies)
    {
        var rate = Rate("Retefuente", 2.5m, minimumBase: 4m, taxUnitValue: 50_000m);

        var totals = DocumentTotals.Compute(
            [new LineAmountsView(Dec(subtotal), 0m, 0m)], FreightAmounts.Zero, appliesVatSurplus: false, [rate]);

        Assert.Equal(200_000m, rate.Threshold);
        Assert.Equal(applies, totals.Withholdings.Count == 1);
        Assert.Equal(applies ? Math.Round(Dec(subtotal) * 0.025m, 2, MidpointRounding.AwayFromZero) : 0m, totals.RetentionAmount);
        Assert.Equal(totals.Total - totals.RetentionAmount, totals.NetTotal);
    }

    [Fact]
    public void NullMinimumBaseAlwaysApplies()
    {
        var totals = DocumentTotals.Compute(
            [new LineAmountsView(1m, 0m, 0m)], FreightAmounts.Zero, appliesVatSurplus: false, [Retefuente]);

        Assert.Equal(0m, Retefuente.Threshold);
        Assert.Equal([new AppliedWithholding(Retefuente, 1m, 0.03m)], totals.Withholdings);
        Assert.Equal(0.03m, totals.RetentionAmount);
    }

    // Sin UVT cargada para el año del documento ni para uno anterior, la tasa con base no aplica.
    [Fact]
    public void MinimumBaseInUvtWithoutValueNeverApplies()
    {
        var rate = Rate("Retefuente", 2.5m, minimumBase: 4m, taxUnitValue: null);

        var totals = DocumentTotals.Compute(
            [new LineAmountsView(999_999_999m, 0m, 0m)], FreightAmounts.Zero, appliesVatSurplus: false, [rate]);

        Assert.Null(rate.Threshold);
        Assert.Empty(totals.Withholdings);
        Assert.Equal(0m, totals.RetentionAmount);
        Assert.Equal(totals.Total, totals.NetTotal);
    }

    // Un país sin unidad tributaria expresa la base en dinero: el umbral es la base misma.
    [Fact]
    public void MinimumBaseOfACountryWithoutTaxUnitIsMoney()
    {
        var rate = Rate("Retefuente", 2.5m, minimumBase: 1_000m, country: "ZZ");

        Assert.Equal(1_000m, rate.Threshold);
    }

    // El umbral no se redondea (spec §7.5): 0,01 UVT × 100.000,01 = 1.000,0001, y 1.000,00 queda por debajo.
    [Fact]
    public void ThresholdIsNotRounded()
    {
        var rate = Rate("ReteICA", 0.966m, minimumBase: 0.01m, taxUnitValue: 100_000.01m);

        var totals = DocumentTotals.Compute(
            [new LineAmountsView(1_000m, 0m, 0m)], FreightAmounts.Zero, appliesVatSurplus: false, [rate]);

        Assert.Equal(1_000.0001m, rate.Threshold);
        Assert.Empty(totals.Withholdings);
    }

    [Fact]
    public void ThresholdIsInclusiveAndFractionRoundsAwayFromZero()
    {
        // 20 UVT × 50.000,05 = 1.000.001: la base cae justo en el umbral y aplica.
        var atThreshold = Rate("ReteICA", 0.966m, minimumBase: 20m, taxUnitValue: 50_000.05m);
        // 750 × 0,966 % = 7,245: con redondeo bancario daría 7,24; lejos de cero, 7,25.
        var midpoint = Rate("ReteICA", 0.966m);

        var atThresholdTotals = DocumentTotals.Compute(
            [new LineAmountsView(1_000_001m, 0m, 0m)], FreightAmounts.Zero, appliesVatSurplus: false, [atThreshold]);
        var midpointTotals = DocumentTotals.Compute(
            [new LineAmountsView(750m, 0m, 0m)], FreightAmounts.Zero, appliesVatSurplus: false, [midpoint]);

        Assert.Equal(1_000_001m, atThreshold.Threshold);
        Assert.Equal([new AppliedWithholding(atThreshold, 1_000_001m, 9_660.01m)], atThresholdTotals.Withholdings);
        Assert.Equal(9_660.01m, atThresholdTotals.RetentionAmount);
        Assert.Equal(7.25m, midpointTotals.RetentionAmount);
    }

    [Fact]
    public void NoWithholdingsMeansNetEqualsTotal()
    {
        var freight = FreightAmounts.For(FreightChargeMode.Charged, 15_000m, 19, "COP", PriceRounding.LegacyFor("COP"));

        var totals = DocumentTotals.Compute(OneLine, freight, appliesVatSurplus: false, []);

        Assert.Empty(totals.Withholdings);
        Assert.Equal(0m, totals.RetentionAmount);
        Assert.Equal(totals.Total, totals.NetTotal);
    }

    [Fact]
    public void ProductsTotalLeavesTheFreightOut()
    {
        var freight = FreightAmounts.For(FreightChargeMode.Charged, 15_000m, 19, "COP", PriceRounding.LegacyFor("COP"));

        var totals = DocumentTotals.Compute(OneLine, freight, appliesVatSurplus: false, []);

        Assert.Equal(119_000m, totals.ProductsTotal);
    }
}
