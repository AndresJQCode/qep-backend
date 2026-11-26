using System.Globalization;
using BuildingBlocks.Domain.Catalog;
using BuildingBlocks.Domain.Pricing;
using BuildingBlocks.Domain.Taxes;
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

    // Spec 2026-11-18 §4.2: un bien de 1.000.000 y un servicio de 500.000, sin IVA.
    private static readonly LineAmountsView[] Mixed =
    [
        new(1_000_000m, 0m, 190_000m, ProductKind.Physical),
        new(500_000m, 0m, 95_000m, ProductKind.Service),
    ];

    private static readonly WithholdingRate RetefuenteGoods =
        Rate("Retefuente", 2.5m) with { AppliesTo = WithholdingScope.Goods };

    private static readonly WithholdingRate RetefuenteServices =
        Rate("Retefuente", 4m) with { AppliesTo = WithholdingScope.Services };

    // Review Focus 1: cada tasa retiene sobre la base de su alcance, y el envío sigue fuera de toda base.
    [Fact]
    public void MixedDocumentWithholdsEachRateOnItsScopeBase()
    {
        var freight = FreightAmounts.For(FreightChargeMode.Charged, 15_000m, 19, "COP", PriceRounding.LegacyFor("COP"));

        var totals = DocumentTotals.Compute(Mixed, freight, appliesVatSurplus: false, [RetefuenteGoods, RetefuenteServices]);

        Assert.Equal(1_500_000m, totals.Subtotal);
        Assert.Equal(
            [new AppliedWithholding(RetefuenteGoods, 1_000_000m, 25_000m), new AppliedWithholding(RetefuenteServices, 500_000m, 20_000m)],
            totals.Withholdings);
        Assert.Equal(45_000m, totals.RetentionAmount);
        Assert.Equal(totals.Total - 45_000m, totals.NetTotal);
    }

    // Review Focus 2: con sólo tasas All el tipo de cada línea no importa; da lo mismo que si todas fueran bienes, que es
    // como se calculaba antes de que la línea tuviera tipo.
    [Fact]
    public void AllScopeRatesReproduceTodaysTotals()
    {
        var all = Rate("Retefuente", 2.5m);
        var freight = FreightAmounts.For(FreightChargeMode.Charged, 15_000m, 19, "COP", PriceRounding.LegacyFor("COP"));
        LineAmountsView[] untyped = [new(1_000_000m, 0m, 190_000m), new(500_000m, 0m, 95_000m)];

        var mixed = DocumentTotals.Compute(Mixed, freight, appliesVatSurplus: false, [all]);
        var today = DocumentTotals.Compute(untyped, freight, appliesVatSurplus: false, [all]);

        Assert.Equal([new AppliedWithholding(all, 1_500_000m, 37_500m)], mixed.Withholdings);
        Assert.Equal(37_500m, mixed.RetentionAmount);
        Assert.Equal(today.Withholdings, mixed.Withholdings);
        Assert.Equal(
            (today.Subtotal, today.TaxAmount, today.Total, today.RetentionAmount, today.NetTotal, today.FreightSubtotal),
            (mixed.Subtotal, mixed.TaxAmount, mixed.Total, mixed.RetentionAmount, mixed.NetTotal, mixed.FreightSubtotal));
    }

    [Fact]
    public void AScopedRateWithoutLinesOfItsKindDoesNotApply()
    {
        var totals = DocumentTotals.Compute(
            [new LineAmountsView(500_000m, 0m, 95_000m, ProductKind.Service)], FreightAmounts.Zero, appliesVatSurplus: false,
            [RetefuenteGoods]);

        Assert.Empty(totals.Withholdings);
        Assert.Equal(0m, totals.RetentionAmount);
    }

    // Cada base se redondea igual que el subtotal del documento.
    [Fact]
    public void BasesAreRoundedLikeTheSubtotal()
    {
        var bases = WithholdingBases.From(
        [
            new LineAmountsView(0.004m, 0m, 0m, ProductKind.Physical),
            new LineAmountsView(0.004m, 0m, 0m, ProductKind.Physical),
            new LineAmountsView(10.005m, 0m, 0m, ProductKind.Service),
        ]);

        Assert.Equal(new WithholdingBases(All: 10.01m, Goods: 0.01m, Services: 10.01m), bases);
        Assert.Equal(
            (10.01m, 0.01m, 10.01m),
            (bases.For(WithholdingScope.All), bases.For(WithholdingScope.Goods), bases.For(WithholdingScope.Services)));
    }

    [Fact]
    public void ProductsTotalLeavesTheFreightOut()
    {
        var freight = FreightAmounts.For(FreightChargeMode.Charged, 15_000m, 19, "COP", PriceRounding.LegacyFor("COP"));

        var totals = DocumentTotals.Compute(OneLine, freight, appliesVatSurplus: false, []);

        Assert.Equal(119_000m, totals.ProductsTotal);
    }

    // ---- Descuento de línea sin IVA (spec 2026-11-23) ----

    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly MemberId Advisor = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    private static Order OrderOf(params (decimal Quantity, decimal UnitPrice, decimal Discount)[] lines) =>
        Order.CreateManual(
            Domain.OrderId.New(), TenantId, "PED-2026-0100",
            new OrderHeader(Guid.CreateVersion7(), Advisor, "COP", "Transferencia", false, null, false, []),
            QuotationParties.Empty,
            new QuotationBillingAccount
            {
                CompanyId = Guid.CreateVersion7(),
                BankName = "Bancolombia",
                AccountNumber = "7542",
                Currency = "COP",
                Country = "CO",
            },
            ShippingDetails.None,
            [.. lines.Select(line => new OrderLineInput(
                Guid.CreateVersion7(), Guid.CreateVersion7(), line.Quantity, line.UnitPrice, line.Discount, 19))],
            null, Advisor, [], Now, DateOnly.FromDateTime(Now.UtcDateTime));

    [Fact]
    public void TheHeaderDiscountSumsTheWithoutVatLines()
    {
        var order = OrderOf((200m, 25_000m, 20m), (3m, 35_900m, 40m));

        Assert.Equal(order.Items.Sum(item => item.DiscountAmount), order.DiscountAmount);
        // Cada línea cierra con su base: bruto sin IVA − descuento = Subtotal.
        Assert.All(order.Items, item =>
        {
            var gross = item.Quantity * item.UnitPrice;
            var grossWithoutTax = gross - Math.Round(gross * 19m / 119m, 2, MidpointRounding.AwayFromZero);
            Assert.Equal(item.Subtotal, grossWithoutTax - item.DiscountAmount);
        });
    }

    // Los totales no se mueven con el cambio de base del descuento: salen de Subtotal y TaxAmount.
    [Fact]
    public void TotalsDoNotChange()
    {
        var order = OrderOf((200m, 25_000m, 20m));

        Assert.Equal(3_361_344.54m, order.Subtotal);
        Assert.Equal(638_655.46m, order.TaxAmount);
        Assert.Equal(4_000_000m, order.Total);
        Assert.Equal(0m, order.RetentionAmount);
        Assert.Equal(4_000_000m, order.NetTotal);
    }

    // POB888: el descuento de encabezado sin IVA es el viejo (con IVA) ÷ 1,19, con ±0,01 por línea.
    [Fact]
    public void TheHeaderDiscountIsTheOldOneDividedByTheVatFactor()
    {
        (decimal Quantity, decimal UnitPrice, decimal Discount)[] lines = [(3m, 35_900m, 40m), (7m, 12_345m, 40m)];
        var cop = PriceRounding.LegacyFor("COP");
        var oldWithVat = lines.Sum(line =>
            VatIncludedLine.Compute(line.Quantity, line.UnitPrice, line.Discount, 19, cop).DiscountAmount);

        var order = OrderOf(lines);

        Assert.InRange(order.DiscountAmount, oldWithVat / 1.19m - 0.02m, oldWithVat / 1.19m + 0.02m);
    }
}
