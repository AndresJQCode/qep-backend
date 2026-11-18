using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El pedido que nace de una orden de compra vinculada (mayorista fase 3): <see cref="Order.CreateFromWholesale"/>, la marca
/// de aprobación (D-1, D-3), la anulación del comprador (D-12, convención de <see cref="Order.CancelByBuyer"/>) y el precio
/// por línea editable (D-4).
/// </summary>
public sealed class WholesaleOrderTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid BuyerTenantId = Guid.CreateVersion7();
    private static readonly Guid PurchaseOrderId = Guid.CreateVersion7();
    private static readonly MemberId Advisor = new(Guid.CreateVersion7());
    private static readonly MemberId Approver = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = Now.AddHours(1);

    private static QuotationBillingAccount Account => new()
    {
        CompanyId = Guid.CreateVersion7(),
        BankName = "Bancolombia",
        AccountNumber = "7542",
        Currency = "COP",
    };

    private static OrderHeader Header() =>
        new(Guid.CreateVersion7(), Advisor, "COP", null, IsRetail: false, null, false, []);

    private static OrderLineInput Line(Guid? sourceLineId = null, decimal unitPrice = 11_900m) =>
        new(
            Guid.CreateVersion7(), Guid.CreateVersion7(), 2m, unitPrice, 0m, 19, new QuotationProductSnapshot("ABC", "Aceite"),
            SourcePurchaseOrderLineId: sourceLineId ?? Guid.CreateVersion7());

    private static QuotationPartyDetails Billing() => new()
    {
        Name = "Distribuidora Andina SAS",
        IdentificationNumber = "900123456",
        IdentificationType = PartyIdentificationType.Nit,
        PersonType = PartyPersonType.Juridica,
        Phone = "+573001112233",
        Email = "compras@andina.example",
        Address = "Calle 1 # 2-3",
        DepartmentId = Guid.CreateVersion7(),
        CityId = Guid.CreateVersion7(),
    };

    private static QuotationParties Parties() =>
        new(Billing(), Billing() with { IdentificationNumber = null, IdentificationType = null, PersonType = null }, BillingVatSurplus: false);

    private static Order NewWholesaleOrder(params OrderLineInput[] lines) =>
        Order.CreateFromWholesale(
            OrderId.New(), TenantId, "PED-2026-0042", Header(), Parties(), Account, lines.Length == 0 ? [Line()] : lines,
            "Entregar en bodega", Advisor, Now, DateOnly.FromDateTime(Now.UtcDateTime), BuyerTenantId, PurchaseOrderId,
            "OC-0007");

    private static Order NewManualOrder() =>
        Order.CreateManual(
            OrderId.New(), TenantId, "PED-2026-0001", Header(), Parties(), Account, ShippingDetails.None, [Line()], null,
            Advisor, [], Now, DateOnly.FromDateTime(Now.UtcDateTime));

    private static void AssertCode(string code, Action act) =>
        Assert.Equal(code, Assert.Throws<QuotationsDomainException>(act).Code);

    [Fact]
    public void CreateFromWholesaleCarriesTheLinkAndTheSourceLineIds()
    {
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();

        var order = NewWholesaleOrder(Line(first), Line(second));

        Assert.Equal(OrderSource.Wholesale, order.Source);
        Assert.Equal("OC-0007", order.ExternalReference);
        Assert.Equal($"purchase-order:{PurchaseOrderId:D}", order.ExternalKey);
        Assert.Equal(Order.WholesaleExternalKey(PurchaseOrderId), order.ExternalKey);
        Assert.Equal(BuyerTenantId, order.BuyerTenantId);
        Assert.Equal(PurchaseOrderId, order.PurchaseOrderId);
        Assert.Equal("Entregar en bodega", order.BuyerNotes);
        Assert.Equal(OrderPaymentStatus.PaymentPending, order.PaymentStatus);
        Assert.True(order.IsAwaitingWholesaleApproval);
        Assert.Null(order.WholesaleApprovedAt);
        Assert.False(order.CancelledByBuyer);
        Assert.Equal([first, second], order.Items.OrderBy(item => item.Position).Select(item => item.SourcePurchaseOrderLineId));
        Assert.All(order.Items, item => Assert.Equal("ABC", item.ProductCode));
    }

    [Fact]
    public void ApproveMarksTheOrderAndBumpsTheVersion()
    {
        var order = NewWholesaleOrder();
        var version = order.Version;

        order.ApproveWholesale(Approver, Later);

        Assert.Equal(Later, order.WholesaleApprovedAt);
        Assert.Equal(Approver, order.WholesaleApprovedBy);
        Assert.False(order.IsAwaitingWholesaleApproval);
        Assert.Equal(OrderStatus.Open, order.Status);
        Assert.Equal(version + 1, order.Version);
        Assert.Equal(Later, order.UpdatedAt);
    }

    [Fact]
    public void ApprovingTwiceIsAlreadyApproved()
    {
        var order = NewWholesaleOrder();
        order.ApproveWholesale(Approver, Later);

        AssertCode("order.wholesale.already_approved", () => order.ApproveWholesale(Approver, Later));
    }

    [Fact]
    public void ApprovingANonWholesaleOrderIsSourceInvalid()
    {
        var order = NewManualOrder();

        AssertCode("order.wholesale.source_invalid", () => order.ApproveWholesale(Approver, Later));
        Assert.False(order.IsAwaitingWholesaleApproval);
    }

    [Fact]
    public void ApprovingACancelledOrderIsNotOpen()
    {
        var order = NewWholesaleOrder();
        order.Cancel(Approver, "Sin stock", Later);

        AssertCode(OrderStateErrors.NotOpen, () => order.ApproveWholesale(Approver, Later));
        Assert.False(order.IsAwaitingWholesaleApproval);
    }

    [Fact]
    public void AnApprovedWholesaleOrderIsNotEditable()
    {
        var order = NewWholesaleOrder();
        order.ApproveWholesale(Approver, Later);

        Assert.False(order.IsEditable);
        AssertCode(
            ExternalOrderErrors.NotEditable,
            () => order.AddItem(
                OrderItemId.New(), Guid.CreateVersion7(), Guid.CreateVersion7(), 1m, 5_000m, 0m, 19, OrderLineCosts.None,
                Approver, Later));
        AssertCode(
            ExternalOrderErrors.NotEditable,
            () => order.ChangeItemUnitPrice(order.Items.First().Id, 10_000m, Approver, Later));
    }

    [Fact]
    public void BeforeApprovalTheWholeOrderIsEditable()
    {
        var order = NewWholesaleOrder(Line(), Line());
        var first = order.Items.OrderBy(item => item.Position).First();
        var second = order.Items.OrderBy(item => item.Position).Last();

        Assert.True(order.IsEditable);
        order.AddItem(
            OrderItemId.New(), Guid.CreateVersion7(), Guid.CreateVersion7(), 1m, 5_000m, 0m, 19, OrderLineCosts.None,
            Approver, Later);
        order.UpdateItemQuantity(first.Id, 5m, 0m, 19, Approver, Later);
        order.RemoveItem(second.Id, Approver, Later);
        order.ChangeItemUnitPrice(first.Id, 10_000m, Approver, Later);

        Assert.Equal(2, order.Items.Count);
        Assert.Equal((5m, 10_000m), (first.Quantity, first.UnitPrice));
    }

    [Fact]
    public void BuyerCancelBeforeApprovalCancels()
    {
        var order = NewWholesaleOrder();
        var version = order.Version;

        Assert.True(order.CancelByWholesaleBuyer(BuyerTenantId, PurchaseOrderId, "Ya no lo necesito", Later));

        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.True(order.CancelledByBuyer);
        Assert.Null(order.CancelledBy);
        Assert.Equal(Later, order.CancelledAt);
        Assert.Equal("Ya no lo necesito", order.CancellationReason);
        Assert.Equal(version + 1, order.Version);
        Assert.False(order.IsAwaitingWholesaleApproval);
    }

    [Fact]
    public void AConsoleCancellationIsNotByTheBuyer()
    {
        var order = NewWholesaleOrder();

        order.Cancel(Approver, "Sin stock", Later);

        Assert.False(order.CancelledByBuyer);
    }

    [Fact]
    public void BuyerCancelAfterApprovalIsRejected()
    {
        var order = NewWholesaleOrder();
        order.ApproveWholesale(Approver, Later);

        AssertCode(
            BuyerOrderErrors.NotCancellableByBuyer,
            () => order.CancelByWholesaleBuyer(BuyerTenantId, PurchaseOrderId, "Ya no", Later));
        Assert.Equal(OrderStatus.Open, order.Status);
    }

    [Fact]
    public void BuyerCancelOfAPreparedOrInvoicedOrderIsRejected()
    {
        var prepared = NewWholesaleOrder();
        prepared.Prepare(Approver, Later);
        AssertCode(
            BuyerOrderErrors.NotCancellableByBuyer,
            () => prepared.CancelByWholesaleBuyer(BuyerTenantId, PurchaseOrderId, "Ya no", Later));

        var invoiced = NewWholesaleOrder();
        invoiced.Invoice(Approver, requireFullPayment: false, Later);
        AssertCode(
            BuyerOrderErrors.NotCancellableByBuyer,
            () => invoiced.CancelByWholesaleBuyer(BuyerTenantId, PurchaseOrderId, "Ya no", Later));

        var archived = NewWholesaleOrder();
        archived.Archive(Approver, Later);
        AssertCode(
            BuyerOrderErrors.NotCancellableByBuyer,
            () => archived.CancelByWholesaleBuyer(BuyerTenantId, PurchaseOrderId, "Ya no", Later));
    }

    [Fact]
    public void BuyerCancelWithAnotherPurchaseOrderIdIsRejected()
    {
        var order = NewWholesaleOrder();

        AssertCode(
            BuyerOrderErrors.NotCancellableByBuyer,
            () => order.CancelByWholesaleBuyer(BuyerTenantId, Guid.CreateVersion7(), "Ya no", Later));
        AssertCode(
            BuyerOrderErrors.NotCancellableByBuyer,
            () => order.CancelByWholesaleBuyer(Guid.CreateVersion7(), PurchaseOrderId, "Ya no", Later));
        AssertCode(
            BuyerOrderErrors.NotCancellableByBuyer,
            () => NewManualOrder().CancelByWholesaleBuyer(BuyerTenantId, PurchaseOrderId, "Ya no", Later));
        Assert.Equal(OrderStatus.Open, order.Status);
    }

    [Fact]
    public void BuyerCancelTwiceReturnsFalse()
    {
        var order = NewWholesaleOrder();
        order.CancelByWholesaleBuyer(BuyerTenantId, PurchaseOrderId, "Ya no", Later);
        var version = order.Version;

        Assert.False(order.CancelByWholesaleBuyer(BuyerTenantId, PurchaseOrderId, "Ya no", Later.AddHours(1)));

        Assert.Equal(version, order.Version);
        Assert.Equal(Later, order.CancelledAt);
    }

    [Fact]
    public void ChangeItemUnitPriceOnAManualOrderIsNotEditable()
    {
        var order = NewManualOrder();

        AssertCode(
            "order.item.unit_price_not_editable",
            () => order.ChangeItemUnitPrice(order.Items.First().Id, 10_000m, Approver, Later));
    }

    [Fact]
    public void ANegativeUnitPriceIsRejected()
    {
        var order = NewWholesaleOrder();

        AssertCode("order.item.unit_price_negative", () => order.ChangeItemUnitPrice(order.Items.First().Id, -1m, Approver, Later));
    }

    [Fact]
    public void ChangeItemUnitPriceRecomputesSubtotalAndTax()
    {
        var order = NewWholesaleOrder(Line(unitPrice: 20_000m));
        var item = order.Items.First();
        var version = order.Version;

        order.ChangeItemUnitPrice(item.Id, 11_900m, Approver, Later);

        Assert.Equal(11_900m, item.UnitPrice);
        Assert.Equal(20_000m, item.Subtotal);
        Assert.Equal(3_800m, item.TaxAmount);
        Assert.Equal(version + 1, order.Version);
    }

    [Fact]
    public void ChangeItemUnitPriceOfOneUnitIsTenThousandPlusVat()
    {
        var line = new OrderLineInput(
            Guid.CreateVersion7(), Guid.CreateVersion7(), 1m, 20_000m, 0m, 19, new QuotationProductSnapshot("ABC", "Aceite"),
            SourcePurchaseOrderLineId: Guid.CreateVersion7());
        var order = NewWholesaleOrder(line);
        var item = order.Items.First();

        order.ChangeItemUnitPrice(item.Id, 11_900m, Approver, Later);

        Assert.Equal((10_000m, 1_900m), (item.Subtotal, item.TaxAmount));
    }
}
