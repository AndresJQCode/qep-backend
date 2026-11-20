using Modules.Purchases.Domain;
using static Modules.Purchases.UnitTests.PurchaseOrderTestData;

namespace Modules.Purchases.UnitTests;

public sealed class LinkedPurchaseOrderTests
{
    private static void AssertCode(string code, Action act) =>
        Assert.Equal(code, Assert.Throws<PurchasesDomainException>(act).Code);

    [Fact]
    public void ALinkedDraftNeedsAListingOnEveryLine()
    {
        Assert.Throws<ArgumentException>(() => LinkedOrder([LinkedLine(), Line()]));
        Assert.Throws<ArgumentException>(() => LinkedOrder([LinkedLine() with { VariantId = Guid.CreateVersion7() }]));
        Assert.Throws<ArgumentException>(() => Order(lines: [LinkedLine() with { VariantId = Guid.CreateVersion7() }]));
        Assert.Throws<ArgumentException>(() => Order(lines: [Line() with { VariantId = null }]));
    }

    [Fact]
    public void ALinkedDraftKeepsTheSupplierTenantAndTheListing()
    {
        var order = LinkedOrder();

        Assert.True(order.IsLinked);
        Assert.Equal(SupplierTenantId, order.SupplierTenantId);
        var line = Assert.Single(order.Lines);
        Assert.Null(line.VariantId);
        Assert.NotNull(line.SupplierVariantId);
        Assert.Equal((12500m, 4.8m, 11900m), (line.ListBasePrice, line.ListDiscountPercent, line.ListUnitPrice));
        Assert.False(Order().IsLinked);
    }

    [Fact]
    public void SendToWholesalerReplacesTheLinesWithTheSnapshotIds()
    {
        var order = LinkedOrder([LinkedLine(code: "A"), LinkedLine(code: "B")]);
        var snapshot = SnapshotOf(order);
        var version = order.Version;

        order.SendToWholesaler(LinkedOrderId, "PED-0042", snapshot, MemberId, Now);

        Assert.Equal(snapshot.Select(line => line.LineId), order.Lines.Select(line => line.Id));
        Assert.Equal(PurchaseOrderStatus.Sent, order.Status);
        Assert.Equal(LinkedOrderId, order.LinkedOrderId);
        Assert.Equal("PED-0042", order.LinkedOrderNumber);
        Assert.Equal(version + 1, order.Version);
        Assert.Equal(PurchaseOrderStatus.Sent, order.History[^1].Status);
    }

    [Fact]
    public void SendRetryAfterTheOrderExistsAdoptsTheOrderLines()
    {
        var order = LinkedOrder([LinkedLine(quantity: 2m)]);

        order.SendToWholesaler(LinkedOrderId, "PED-0042", SnapshotOf(order, quantity: 5m), MemberId, Now);

        Assert.Equal(5m, Assert.Single(order.Lines).Quantity);
    }

    [Fact]
    public void SendToWholesalerOnAnExternalOrderIsSupplierNotLinked() =>
        AssertCode(
            "purchases.order.supplier_not_linked",
            () => Order().SendToWholesaler(LinkedOrderId, "PED-0042", [], MemberId, Now));

    [Fact]
    public void ALinkedOrderIsNotSentWithThePlainSend() =>
        Assert.Throws<InvalidOperationException>(() => LinkedOrder().Send(MemberId, Now));

    [Fact]
    public void ALinkedSentOrderOnlyAllowsCancel()
    {
        var order = SentLinkedOrder();

        Assert.Equal([PurchaseOrderAction.Cancel], order.AllowedActions());
        AssertCode(
            "purchases.order.invalid_transition",
            () => order.Confirm(AsRequested(order), MemberId, Now));
    }

    [Fact]
    public void ALinkedDraftAllowsEditDeleteSendAndCancel() =>
        Assert.Equal(
            [PurchaseOrderAction.Edit, PurchaseOrderAction.Delete, PurchaseOrderAction.Send, PurchaseOrderAction.Cancel],
            LinkedOrder().AllowedActions());

    [Fact]
    public void ALinkedConfirmedOrderOnlyAllowsReceive()
    {
        var order = SentLinkedOrder();
        order.ApplyWholesalerApproval(order.Lines.Select(line => FinalOf(line)).ToList(), Now);

        Assert.Equal([PurchaseOrderAction.Receive], order.AllowedActions());
        order.Receive(MemberId, Now);
        Assert.Empty(order.AllowedActions());
    }

    [Fact]
    public void AnExternalOrderKeepsPhaseOneActions() =>
        Assert.Equal([PurchaseOrderAction.Confirm, PurchaseOrderAction.Cancel], OrderIn(PurchaseOrderStatus.Sent).AllowedActions());

    [Fact]
    public void ALinkedDraftWithoutPricesKeepsQuantitiesAndDropsEveryAmount()
    {
        var order = LinkedOrder([LinkedLine(quantity: 3m)], withPrices: false);

        var line = Assert.Single(order.Lines);
        Assert.Equal(3m, line.Quantity);
        Assert.NotNull(line.SupplierVariantId);
        Assert.Equal("SKU-1", line.Code);
        Assert.Equal(19m, line.TaxRatePercent);
        Assert.Null(line.UnitCost);
        Assert.Null(line.ListBasePrice);
        Assert.Null(line.ListDiscountPercent);
        Assert.Null(line.ListUnitPrice);
        Assert.Null(line.Subtotal);
        Assert.Null(line.Tax);
        Assert.Null(order.Totals());
        Assert.Null(order.RequestedTotals());
        Assert.False(order.HasPrices);
    }

    [Fact]
    public void AnExternalOrderNeverDropsItsAmounts() =>
        Assert.Throws<ArgumentException>(() =>
            PurchaseOrder.Create(TenantId, "OC-0001", SupplierId, null, "COP", null, [Line()], MemberId, Now, withPrices: false));

    [Fact]
    public void ReplaceWithPricesGivesAPricelessDraftItsAmounts()
    {
        var order = LinkedOrder(withPrices: false);

        order.Replace(SupplierId, SupplierTenantId, "COP", null, [LinkedLine()], Now.AddMinutes(1), withPrices: true);

        Assert.True(order.HasPrices);
        Assert.Equal(10000m, order.Lines[0].UnitCost);
        Assert.NotNull(order.Totals());
    }

    [Fact]
    public void ReplaceWithoutPricesTakesTheAmountsFromAPricedDraft()
    {
        var order = LinkedOrder();

        order.Replace(SupplierId, SupplierTenantId, "COP", null, [LinkedLine()], Now.AddMinutes(1), withPrices: false);

        Assert.False(order.HasPrices);
        Assert.Null(order.Lines[0].UnitCost);
        Assert.Null(order.Lines[0].ListUnitPrice);
    }

    [Fact]
    public void ReplaceOfAnExternalOrderWithoutPricesIsAProgrammingError() =>
        Assert.Throws<ArgumentException>(() =>
            Order().Replace(SupplierId, null, "COP", null, [Line()], Now, withPrices: false));

    [Fact]
    public void SendToWholesalerWithoutPricesAdoptsTheLinesWithoutAmountsEvenIfTheDraftHadThem()
    {
        var order = LinkedOrder();
        var snapshot = SnapshotOf(order);

        order.SendToWholesaler(LinkedOrderId, "PED-0042", snapshot, MemberId, Now, withPrices: false);

        Assert.Equal(snapshot.Select(line => line.LineId), order.Lines.Select(line => line.Id));
        var line = Assert.Single(order.Lines);
        Assert.Equal(2m, line.Quantity);
        Assert.Null(line.UnitCost);
        Assert.Null(line.ListBasePrice);
        Assert.Null(line.ListDiscountPercent);
        Assert.Null(line.ListUnitPrice);
        Assert.False(order.HasPrices);
    }

    [Fact]
    public void SendToWholesalerWithPricesAdoptsTheAmountsOfAPricelessDraft()
    {
        var order = LinkedOrder(withPrices: false);

        order.SendToWholesaler(LinkedOrderId, "PED-0042", SnapshotOf(order), MemberId, Now, withPrices: true);

        Assert.True(order.HasPrices);
        Assert.Equal(10000m, order.Lines[0].UnitCost);
        Assert.Equal(11900m, order.Lines[0].ListUnitPrice);
    }

    [Fact]
    public void ApprovingAPricelessOrderGivesItConfirmedPricesButNoRequestedTotal()
    {
        var order = SentPricelessOrder([LinkedLine(quantity: 120m)]);
        var line = order.Lines[0];

        order.ApplyWholesalerApproval([FinalOf(line, quantity: 100m, unitCost: 10000m)], Now);

        Assert.True(order.HasPrices);
        Assert.Null(line.UnitCost);
        Assert.Equal(10000m, line.ConfirmedUnitCost);
        Assert.Equal(PurchaseOrderLineStatus.Changed, line.Status);
        Assert.Equal(new PurchaseOrderTotals(1_000_000m, 190_000m, 1_190_000m), order.Totals());
        Assert.Null(order.RequestedTotals());
        Assert.True(order.ConfirmedWithChanges);
    }

    [Fact]
    public void APricelessLineWithTheSameQuantityIsUnchangedWhateverThePriceTurnsOutToBe()
    {
        var order = SentPricelessOrder([LinkedLine(quantity: 5m)]);

        order.ApplyWholesalerApproval([FinalOf(order.Lines[0], unitCost: 8765m)], Now);

        Assert.Equal(PurchaseOrderLineStatus.Unchanged, order.Lines[0].Status);
        Assert.False(order.ConfirmedWithChanges);
        Assert.True(order.HasPrices);
    }

    [Fact]
    public void ARemovedPricelessLineIsRejectedWithASubtotalOfZero()
    {
        var order = SentPricelessOrder([LinkedLine(code: "A"), LinkedLine(code: "B")]);
        var kept = order.Lines[0];
        var dropped = order.Lines[1];

        order.ApplyWholesalerApproval([FinalOf(kept, unitCost: 10000m)], Now);

        Assert.Equal(PurchaseOrderLineStatus.Rejected, dropped.Status);
        Assert.Equal(0m, dropped.Subtotal);
        Assert.Null(dropped.ConfirmedUnitCost);
        Assert.True(order.HasPrices);
    }

    [Fact]
    public void ALineTheWholesalerAddedToAPricelessOrderIsAddedWithItsPrice()
    {
        var order = SentPricelessOrder();
        var added = new WholesalerFinalLine(null, Guid.CreateVersion7(), "NEW", "Nueva", 4m, 2500m, 19m);

        order.ApplyWholesalerApproval([FinalOf(order.Lines[0], unitCost: 10000m), added], Now);

        Assert.Equal(PurchaseOrderLineStatus.Added, order.Lines[1].Status);
        Assert.Equal(10000m, order.Lines[1].Subtotal);
        Assert.True(order.HasPrices);
    }

    [Fact]
    public void ACancelledPricelessOrderStaysWithoutPrices()
    {
        var order = SentPricelessOrder();

        order.Cancel(MemberId, Now);

        Assert.False(order.HasPrices);
        Assert.Null(order.Totals());
    }

    [Fact]
    public void APricedOrderHasPricesAndAnExternalOneAlways()
    {
        Assert.True(LinkedOrder().HasPrices);
        Assert.True(SentLinkedOrder().HasPrices);
        Assert.True(Order().HasPrices);
    }

    [Theory]
    [InlineData(11900, 19, 10000)]
    [InlineData(10000, 19, 8403.36)]
    [InlineData(5000, 0, 5000)]
    public void NetOfVatRoundsToTwoDecimals(decimal gross, decimal rate, decimal expected) =>
        Assert.Equal(expected, PurchaseOrderMath.NetOfVat(gross, rate));
}
