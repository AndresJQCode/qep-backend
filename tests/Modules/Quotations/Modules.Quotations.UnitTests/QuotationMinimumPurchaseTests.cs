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

    // El envío cobrado suma al total del documento, pero no es compra: no ayuda a llegar al mínimo.
    [Fact]
    public void FreightDoesNotHelpReachTheMinimum()
    {
        var advisor = new MemberId(Guid.CreateVersion7());
        var now = new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
        var quotation = Quotation.Create(
            QuotationId.New(), Guid.CreateVersion7(), "QUO-2026-0001", Guid.CreateVersion7(), advisor,
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
        var settings = QuotationSettings.Create(6, new Dictionary<string, decimal> { ["COP"] = 200_000m });

        Assert.Equal(210_000m, quotation.Total);
        Assert.False(QuotationMinimumPurchase.IsSatisfiedBy(quotation, settings));
    }
}
