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

    [Theory]
    [InlineData(11900, 19, 10000)]
    [InlineData(10000, 19, 8403.36)]
    [InlineData(5000, 0, 5000)]
    public void NetOfVatRoundsToTwoDecimals(decimal gross, decimal rate, decimal expected) =>
        Assert.Equal(expected, PurchaseOrderMath.NetOfVat(gross, rate));
}
