using Modules.Quotations.Application;
using Modules.Quotations.Domain;

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

    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();

    private static Quotation QuotationWithChargedFreight()
    {
        var advisor = new MemberId(Guid.CreateVersion7());
        var now = new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
        var quotation = Quotation.Create(
            QuotationId.New(), TenantId, "QUO-2026-0001", ClientId, advisor,
            new DateOnly(2026, 12, 31), paymentMethod: "Transferencia bancaria", notes: null, QuotationParties.Empty,
            new QuotationBillingAccount
            {
                CompanyId = Guid.CreateVersion7(),
                BankName = "Bancolombia",
                AccountNumber = "12345678",
                Currency = "COP"
            },
            "COP", customerWithRetention: false, customerVatSurplus: false, advisor, now);
        quotation.AddItem(QuotationItemId.New(), Guid.CreateVersion7(), 1m, 150_000m, 0m, 19, advisor, now);
        quotation.SetShipping(
            new ShippingDetails(null, null, FreightChargeMode.Charged, 60_000m, 19, false), advisor, now);
        return quotation;
    }

    private static readonly QuotationSettings Min200 =
        QuotationSettings.Create(6, new Dictionary<string, decimal> { ["COP"] = 200_000m });

    // El envío cobrado suma al total del documento, pero no es compra: no ayuda a llegar al mínimo.
    [Fact]
    public void FreightDoesNotHelpReachTheMinimum()
    {
        var quotation = QuotationWithChargedFreight();

        Assert.Equal(210_000m, quotation.Total);
        Assert.False(QuotationMinimumPurchase.IsSatisfiedBy(quotation, Min200));
    }

    // Lo que la pantalla pinta: el estado del mínimo tampoco cuenta el envío.
    [Fact]
    public async Task TheResponseMinimumStateIgnoresTheFreight()
    {
        var quotation = QuotationWithChargedFreight();
        var composer = new QuotationResponseComposer(
            new StubQuotationCustomerLookup(new QuotationCustomerRef(
                ClientId, TenantId, "CUC-001", IsActive: true, "Ferretería El Tornillo",
                "3001234567", "Calle 1 # 2-3", WithRetention: false, VatSurplus: false)),
            new StubQuotationAdvisorLookup("asesora@qcode.co", "Asesora Uno"),
            new StubQuotationProductLookup(new Dictionary<Guid, QuotationProductRef>()),
            new StubQuotationCompanyLookup(new Dictionary<Guid, QuotationCompanyRef>()),
            new FixedQuotationSettingsStore(Min200));

        var response = await composer.ComposeAsync(TenantId, quotation.ToDto(), TestContext.Current.CancellationToken);

        Assert.Equal(210_000m, response.Total);
        Assert.False(response.MinimumPurchase.Met);
        Assert.Equal(50_000m, response.MinimumPurchase.MissingTotal);
    }
}
