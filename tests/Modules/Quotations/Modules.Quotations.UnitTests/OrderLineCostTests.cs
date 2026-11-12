using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>El costo congelado de las líneas del pedido y el instante en que quedó pagado (spec costo de producto
/// 2026-11-10 §4.2–§4.4): cada línea congela una sola vez, la primera vez que cuenta como vendida.</summary>
public sealed class OrderLineCostTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly MemberId Advisor = new(Guid.CreateVersion7());
    private static readonly Guid V1 = Guid.CreateVersion7();
    private static readonly Guid V2 = Guid.CreateVersion7();
    private static readonly Guid V3 = Guid.CreateVersion7();
    private static readonly DateTimeOffset T1 = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T2 = T1.AddHours(1);
    private static readonly DateTimeOffset T3 = T1.AddHours(2);

    private static QuotationBillingAccount Account => new()
    {
        CompanyId = Guid.CreateVersion7(),
        BankName = "Bancolombia",
        AccountNumber = "7542",
        Currency = "COP",
    };

    private static OrderHeader Header() =>
        new(ClientId, Advisor, "COP", "Transferencia", false, null, false, false);

    private static OrderLineInput Line(Guid variantId) =>
        new(variantId, Guid.CreateVersion7(), 2m, 10_000m, 0m, 19, new QuotationProductSnapshot("ABC", "Aceite"));

    private static OrderLineCosts Costs(decimal v1Cost) =>
        OrderLineCosts.Of([V1, V2], new Dictionary<Guid, decimal> { [V1] = v1Cost });

    // Dos líneas de 2 × 10.000 → neto 40.000.
    private static Order NewOrder(IReadOnlyCollection<OrderPaymentProofInput>? proofs = null) =>
        Order.CreateManual(
            OrderId.New(), TenantId, "PED-2026-0001", Header(), QuotationParties.Empty, Account,
            ShippingDetails.None, [Line(V1), Line(V2)], null, Advisor, proofs ?? [], T1);

    private static Order External(OrderPaymentStatus paymentStatus) =>
        Order.CreateExternal(
            OrderId.New(), TenantId, "PED-2026-0002", Header(), QuotationParties.Empty, Account, [Line(V1), Line(V2)],
            paymentStatus, null, Advisor, [], T1, OrderSource.Shopify, "#1001", ShippingDetails.None, "shop:1001", null,
            Costs(6000m));

    private static OrderItem Item(Order order, Guid variantId) => order.Items.Single(item => item.VariantId == variantId);

    private static void AddV3(Order order, OrderLineCosts costs, DateTimeOffset at) =>
        order.AddItem(OrderItemId.New(), V3, Guid.CreateVersion7(), 1m, 4_000m, 0m, 19, costs, Advisor, at);

    [Fact]
    public void MarkPaidFreezesEveryLineOnce()
    {
        var order = NewOrder();

        order.MarkPaid(Advisor, requirePaymentProof: false, Costs(6000m), T1);

        Assert.Equal(6000m, Item(order, V1).UnitCost);
        Assert.Equal(LineCostSource.Manual, Item(order, V1).CostSource);
        Assert.Equal(T1, Item(order, V1).CostCapturedAt);
        Assert.Null(Item(order, V2).UnitCost);
        Assert.Equal(LineCostSource.Manual, Item(order, V2).CostSource);
        Assert.Equal(T1, Item(order, V2).CostCapturedAt);
        Assert.Equal(T1, order.PaidAt);
        Assert.Empty(order.UncostedVariantIds);
    }

    [Fact]
    public void UnmarkAndRemarkKeepTheFrozenCostAndMoveThePaidAt()
    {
        var order = NewOrder();
        order.MarkPaid(Advisor, requirePaymentProof: false, Costs(6000m), T1);

        order.UnmarkPaid(T2);

        Assert.Null(order.PaidAt);
        Assert.Equal(6000m, Item(order, V1).UnitCost);
        Assert.Equal(T1, Item(order, V1).CostCapturedAt);

        order.MarkPaid(Advisor, requirePaymentProof: false, Costs(9000m), T3);

        Assert.Equal(6000m, Item(order, V1).UnitCost);
        Assert.Equal(T1, Item(order, V1).CostCapturedAt);
        Assert.Equal(T3, order.PaidAt);
    }

    [Fact]
    public void PartialPaymentDoesNotFreezeAndMarkPaidDoes()
    {
        var order = NewOrder();

        order.AddPaymentProofs([new OrderPaymentProofInput(Guid.CreateVersion7(), 20_000m)], null, Advisor, T1);

        Assert.Equal(OrderPaymentStatus.PartialPaymentReceived, order.PaymentStatus);
        Assert.All(order.Items, item => Assert.Null(item.CostCapturedAt));
        Assert.Null(order.PaidAt);
        Assert.Equal(new[] { V1, V2 }, order.UncostedVariantIds);

        order.MarkPaid(Advisor, requirePaymentProof: false, Costs(6000m), T2);

        Assert.All(order.Items, item => Assert.Equal(T2, item.CostCapturedAt));
        Assert.Equal(T2, order.PaidAt);
    }

    [Fact]
    public void ALineAddedToAPaidOrderFreezesOnArrival()
    {
        var order = NewOrder();
        order.MarkPaid(Advisor, requirePaymentProof: false, Costs(6000m), T1);

        AddV3(order, OrderLineCosts.Of([V3], new Dictionary<Guid, decimal> { [V3] = 500m }), T2);

        Assert.Equal(500m, Item(order, V3).UnitCost);
        Assert.Equal(LineCostSource.Manual, Item(order, V3).CostSource);
        Assert.Equal(T2, Item(order, V3).CostCapturedAt);
        Assert.Equal(T1, Item(order, V1).CostCapturedAt);
        Assert.Equal(T1, Item(order, V2).CostCapturedAt);
    }

    [Fact]
    public void ALineAddedToAnUnpaidOrderDoesNotFreeze()
    {
        var order = NewOrder();

        AddV3(order, OrderLineCosts.None, T2);

        Assert.Null(Item(order, V3).CostCapturedAt);
        Assert.Null(Item(order, V3).CostSource);
    }

    [Fact]
    public void AddingToAPaidOrderWithoutItsCostIsAProgrammingError()
    {
        var order = NewOrder();
        order.MarkPaid(Advisor, requirePaymentProof: false, Costs(6000m), T1);

        Assert.Throws<InvalidOperationException>(() => AddV3(order, OrderLineCosts.None, T2));
    }

    [Fact]
    public void MarkingPaidWithoutTheCostOfALineIsAProgrammingError()
    {
        var order = NewOrder();

        Assert.Throws<InvalidOperationException>(
            () => order.MarkPaid(Advisor, requirePaymentProof: false, OrderLineCosts.None, T1));
    }

    [Fact]
    public void ChangingTheQuantityKeepsTheUnitCost()
    {
        var order = NewOrder();
        order.MarkPaid(Advisor, requirePaymentProof: false, Costs(6000m), T1);

        order.UpdateItemQuantity(Item(order, V1).Id, 5m, 0m, 19, Advisor, T2);

        Assert.Equal(6000m, Item(order, V1).UnitCost);
        Assert.Equal(T1, Item(order, V1).CostCapturedAt);
    }

    [Fact]
    public void AnExternalOrderBornPaidFreezesAtCreation()
    {
        var paid = External(OrderPaymentStatus.FullPaymentReceived);

        Assert.Equal(6000m, Item(paid, V1).UnitCost);
        Assert.All(paid.Items, item => Assert.Equal(T1, item.CostCapturedAt));
        Assert.Equal(T1, paid.PaidAt);

        var pending = External(OrderPaymentStatus.PaymentPending);

        Assert.All(pending.Items, item => Assert.Null(item.CostCapturedAt));
        Assert.Null(pending.PaidAt);
    }

    [Fact]
    public void SyncingToPaidFreezesAndSetsPaidAt()
    {
        var order = External(OrderPaymentStatus.PaymentPending);

        order.SyncExternalPaymentStatus(OrderPaymentStatus.FullPaymentReceived, autoArchive: false, Costs(6000m), T1);

        Assert.Equal(6000m, Item(order, V1).UnitCost);
        Assert.All(order.Items, item => Assert.Equal(T1, item.CostCapturedAt));
        Assert.Equal(T1, order.PaidAt);
    }

    [Fact]
    public void SyncingPaidTwiceDoesNotRefreezeTheLines()
    {
        var order = External(OrderPaymentStatus.PaymentPending);
        order.SyncExternalPaymentStatus(OrderPaymentStatus.FullPaymentReceived, autoArchive: false, Costs(6000m), T1);

        order.SyncExternalPaymentStatus(
            OrderPaymentStatus.PartialPaymentReceived, autoArchive: false, OrderLineCosts.None, T2);

        Assert.Null(order.PaidAt);
        Assert.Equal(6000m, Item(order, V1).UnitCost);
        Assert.Equal(T1, Item(order, V1).CostCapturedAt);

        order.SyncExternalPaymentStatus(OrderPaymentStatus.FullPaymentReceived, autoArchive: false, Costs(9000m), T3);

        Assert.Equal(6000m, Item(order, V1).UnitCost);
        Assert.Equal(T1, Item(order, V1).CostCapturedAt);
        Assert.Equal(T3, order.PaidAt);
    }

    [Fact]
    public void DroppingToPartialAfterAnEditClearsPaidAtAndKeepsCosts()
    {
        var order = NewOrder([new OrderPaymentProofInput(Guid.CreateVersion7(), 40_000m)]);
        order.MarkPaid(Advisor, requirePaymentProof: true, Costs(6000m), T1);
        var previousNet = order.NetTotal;

        AddV3(order, OrderLineCosts.Of([V3], new Dictionary<Guid, decimal> { [V3] = 500m }), T2);
        order.RecalculatePaymentStatus(previousNet, T2);

        Assert.Equal(OrderPaymentStatus.PartialPaymentReceived, order.PaymentStatus);
        Assert.Null(order.PaidAt);
        Assert.Equal(6000m, Item(order, V1).UnitCost);
        Assert.Equal(T1, Item(order, V1).CostCapturedAt);
        Assert.Equal(500m, Item(order, V3).UnitCost);
    }

    // Revisión de la tarea 5: si falta el costo de una línea, ninguna congela; nada de pedido a medias.
    [Fact]
    public void AMissingCostFreezesNoLine()
    {
        var order = NewOrder();
        var onlyV1 = OrderLineCosts.Of([V1], new Dictionary<Guid, decimal> { [V1] = 6000m });

        Assert.Throws<InvalidOperationException>(
            () => order.MarkPaid(Advisor, requirePaymentProof: false, onlyV1, T1));

        Assert.All(order.Items, item =>
        {
            Assert.Null(item.UnitCost);
            Assert.Null(item.CostSource);
            Assert.Null(item.CostCapturedAt);
        });
        Assert.Null(order.PaidAt);
        Assert.NotEqual(OrderPaymentStatus.FullPaymentReceived, order.PaymentStatus);
    }

    // Anular no saca al pedido de pagado: el reporte lo excluye por estado, no porque pierda su fecha.
    [Fact]
    public void CancellingKeepsPaidAtAndTheFrozenCosts()
    {
        var order = NewOrder();
        order.MarkPaid(Advisor, requirePaymentProof: false, Costs(6000m), T1);

        order.Cancel(Advisor, "El cliente desistió.", T2);

        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Equal(T1, order.PaidAt);
        Assert.Equal(6000m, Item(order, V1).UnitCost);
    }

    // Un costo entregado siempre cuenta como buscado: pasarlo en found sin repetirlo en lookedUp no puede romper.
    [Fact]
    public void ACostSuppliedWithoutBeingListedAsLookedUpStillCounts()
    {
        var order = NewOrder();
        var costs = OrderLineCosts.Of([V2], new Dictionary<Guid, decimal> { [V1] = 6000m });

        order.MarkPaid(Advisor, requirePaymentProof: false, costs, T1);

        Assert.Equal(6000m, Item(order, V1).UnitCost);
        Assert.Null(Item(order, V2).UnitCost);
        Assert.Equal(T1, Item(order, V2).CostCapturedAt);
    }
}
