using Modules.Purchases.Domain;
using static Modules.Purchases.UnitTests.PurchaseOrderTestData;

namespace Modules.Purchases.UnitTests;

public sealed class PurchaseOrderTotalsTests
{
    [Fact]
    public void TotalsAddTaxOnTopOfTheCostWithoutTax()
    {
        var order = Order(lines: [Line(quantity: 3m, unitCost: 10.00m, taxRatePercent: 19m), Line(quantity: 1m, unitCost: 5.55m, taxRatePercent: 0m)]);

        Assert.Equal(new PurchaseOrderTotals(35.55m, 5.70m, 41.25m), order.Totals());
        Assert.Equal(30.00m, order.Lines[0].Subtotal);
        Assert.Equal(5.70m, order.Lines[0].Tax);
    }

    [Fact]
    public void AnOrderWithAPricelessLineHasNoTotalsAndOneWithAllPricesStillDoes()
    {
        var order = LinkedOrder([LinkedLine(code: "A"), LinkedLine(code: "B")], withPrices: false);
        Assert.Null(order.Totals());
        Assert.Null(order.RequestedTotals());

        order.Replace(SupplierId, SupplierTenantId, "COP", null, [LinkedLine(quantity: 3m, unitCost: 100m, taxRatePercent: 0m)], Now);

        Assert.Equal(new PurchaseOrderTotals(300m, 0m, 300m), order.Totals());
        Assert.Equal(new PurchaseOrderTotals(300m, 0m, 300m), order.RequestedTotals());
    }

    [Fact]
    public void LineTaxIsRoundedAwayFromZeroPerLine()
    {
        Assert.Equal(0.03m, PurchaseOrderMath.Tax(0.25m, 10m));
        Assert.Equal(0.13m, PurchaseOrderMath.Subtotal(0.5m, 0.25m));
    }

    [Fact]
    public void SumOfAnEmptyListIsZero() =>
        Assert.Equal(new PurchaseOrderTotals(0m, 0m, 0m), PurchaseOrderMath.Sum([]));

    [Fact]
    public void TotalsUseConfirmedValuesOnceConfirmedAndRequestedStaysOriginal()
    {
        var order = Order(lines: [Line(quantity: 2m, unitCost: 10m, taxRatePercent: 0m)]);
        Assert.Equal(20m, order.Totals()!.Subtotal);
        order.Send(MemberId, Now);

        order.Confirm([new PurchaseOrderLineConfirmation(order.Lines[0].Id, 1m, 12m)], MemberId, Now);

        Assert.Equal(12m, order.Totals()!.Subtotal);
        Assert.Equal(12m, order.Lines[0].Subtotal);
        Assert.Equal(20m, order.Lines[0].RequestedSubtotal);
        Assert.Equal(20m, order.RequestedTotals()!.Subtotal);
    }

    [Fact]
    public void ARejectedLineAddsNothingToTheConfirmedTotals()
    {
        var order = Order(lines: [Line(quantity: 2m, unitCost: 10m, taxRatePercent: 19m), Line(quantity: 1m, unitCost: 100m, taxRatePercent: 19m)]);
        order.Send(MemberId, Now);

        order.Confirm(
            [
                new PurchaseOrderLineConfirmation(order.Lines[0].Id, 2m, 10m),
                new PurchaseOrderLineConfirmation(order.Lines[1].Id, 0m, 100m),
            ],
            MemberId,
            Now);

        Assert.Equal(new PurchaseOrderTotals(20m, 3.80m, 23.80m), order.Totals());
        Assert.Equal(new PurchaseOrderTotals(120m, 22.80m, 142.80m), order.RequestedTotals());
    }

    [Fact]
    public void ACancelledAfterConfirmOrderKeepsTheConfirmedTotals()
    {
        var order = Order(lines: [Line(quantity: 2m, unitCost: 10m, taxRatePercent: 19m)]);
        order.Send(MemberId, Now);
        order.Confirm([new PurchaseOrderLineConfirmation(order.Lines[0].Id, 1m, 10m)], MemberId, Now);
        var confirmed = order.Totals();

        order.Cancel(MemberId, Now);

        Assert.Equal(new PurchaseOrderTotals(10m, 1.90m, 11.90m), confirmed);
        Assert.Equal(confirmed, order.Totals());
    }

    [Fact]
    public void RequestedTotalsIgnoreAddedLines()
    {
        var order = SentLinkedOrder([LinkedLine(quantity: 2m, unitCost: 100m, taxRatePercent: 0m)]);
        var sent = order.Lines[0];

        order.ApplyWholesalerApproval(
            [FinalOf(sent), new WholesalerFinalLine(null, Guid.CreateVersion7(), "NEW", "Nueva", 3m, 50m, 0m)],
            Now);

        Assert.Equal(0m, order.Lines[1].RequestedSubtotal);
        Assert.Equal(new PurchaseOrderTotals(200m, 0m, 200m), order.RequestedTotals());
        Assert.Equal(new PurchaseOrderTotals(350m, 0m, 350m), order.Totals());
    }
}
