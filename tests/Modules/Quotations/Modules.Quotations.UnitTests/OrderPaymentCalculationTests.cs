using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Spec 2026-11-10 §3.2: el pago lo calcula el sistema con los comprobantes y nunca llega solo a
/// <see cref="OrderPaymentStatus.FullPaymentReceived"/>: eso lo da «Marcar como pagado» o la plataforma.
/// Neto por defecto 23.800 COP (2 × 11.900 con IVA del 19 %), como en <see cref="OrderTests"/>.
/// </summary>
public sealed class OrderPaymentCalculationTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly MemberId Advisor = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private static QuotationBillingAccount Account => new()
    {
        CompanyId = Guid.CreateVersion7(),
        BankName = "Bancolombia",
        AccountNumber = "7542",
        Currency = "COP",
    };

    private static OrderHeader Header(bool withRetention = false) =>
        new(ClientId, Advisor, "COP", "Transferencia", false, null, withRetention, false);

    private static OrderLineInput Line(decimal quantity = 2m, decimal unitPrice = 11_900m) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), quantity, unitPrice, 0m, 19, new QuotationProductSnapshot("ABC", "Aceite"));

    private static OrderPaymentProofInput[] Proofs(params decimal[] amounts) =>
        amounts.Select(amount => new OrderPaymentProofInput(Guid.CreateVersion7(), amount)).ToArray();

    private static Order NewOrder(
        IReadOnlyCollection<OrderPaymentProofInput> proofs,
        IReadOnlyCollection<OrderLineInput>? lines = null,
        OrderHeader? header = null) =>
        Order.CreateManual(
            OrderId.New(), TenantId, "PED-2026-0001", header ?? Header(), QuotationParties.Empty, Account,
            ShippingDetails.None, lines ?? [Line()], null, Advisor, proofs, Now);

    private static Order ExternalOrder() =>
        Order.CreateExternal(
            OrderId.New(), TenantId, "PED-2026-0002", Header(), QuotationParties.Empty, Account, [Line()],
            OrderPaymentStatus.PaymentPending, null, Advisor, [], Now, OrderSource.Shopify, "#1001",
            ShippingDetails.None, "shop:1001", null);

    private static Order Converted(IReadOnlyCollection<OrderPaymentProofInput> proofs)
    {
        var quotation = Quotation.Create(
            QuotationId.New(), TenantId, "COT-2026-0001", ClientId, Advisor, new DateOnly(2026, 12, 1), "Transferencia",
            null, QuotationParties.Empty, Account, "COP", customerWithRetention: false, customerVatSurplus: false, Advisor, Now);
        quotation.AddItem(QuotationItemId.New(), Guid.CreateVersion7(), Guid.CreateVersion7(), 2m, 11_900m, 0m, 19, Advisor, Now);
        quotation.ConvertToOrder(Advisor, Now, new Dictionary<Guid, QuotationProductSnapshot>
        {
            [quotation.Items.Single().VariantId] = new("ABC", "Aceite"),
        });

        return Order.FromQuotation(OrderId.New(), TenantId, "PED-2026-0001", quotation, null, Advisor, proofs, Now);
    }

    private static void AddSameLine(Order order) =>
        order.AddItem(OrderItemId.New(), Guid.CreateVersion7(), Guid.CreateVersion7(), 2m, 11_900m, 0m, 19, Advisor, Now);

    [Fact]
    public void ConvertingWithProofsCoveringTheNetLeavesItPendingVerification() =>
        Assert.Equal(OrderPaymentStatus.PendingVerification, Converted(Proofs(100_000m)).PaymentStatus);

    [Fact]
    public void ConvertingWithAPartialProofIsPartial() =>
        Assert.Equal(OrderPaymentStatus.PartialPaymentReceived, Converted(Proofs(20_000m)).PaymentStatus);

    [Fact]
    public void ConvertingWithoutProofsIsPaymentPending() =>
        Assert.Equal(OrderPaymentStatus.PaymentPending, Converted([]).PaymentStatus);

    [Fact]
    public void AddingProofsNeverPromotesToPaid()
    {
        var order = NewOrder([]);

        var change = order.AddPaymentProofs(Proofs(30_000m), null, Advisor, Now);

        Assert.Equal(OrderPaymentStatus.PendingVerification, order.PaymentStatus);
        Assert.Equal(
            new OrderPaymentStatusChange(OrderPaymentStatus.PaymentPending, OrderPaymentStatus.PendingVerification, 23_800m, 23_800m),
            change);
    }

    [Fact]
    public void AddingProofsToAPaidOrderKeepsItPaid()
    {
        var order = NewOrder(Proofs(100_000m));
        order.MarkPaid(Advisor, requirePaymentProof: false, Now);

        var change = order.AddPaymentProofs(Proofs(1m), null, Advisor, Now);

        Assert.Equal(OrderPaymentStatus.FullPaymentReceived, order.PaymentStatus);
        Assert.Null(change);
    }

    [Fact]
    public void RecalculatingNeverPromotesToPaid()
    {
        var order = NewOrder(Proofs(100_000m));

        var change = order.RecalculatePaymentStatus(23_800m, Now);

        Assert.Equal(OrderPaymentStatus.PendingVerification, order.PaymentStatus);
        Assert.Null(change);
    }

    [Fact]
    public void AnEditThatRaisesTheNetAboveTheDepositedDemotesAPaidOrder()
    {
        var order = NewOrder(Proofs(23_800m));
        order.MarkPaid(Advisor, requirePaymentProof: false, Now);
        AddSameLine(order);

        var change = order.RecalculatePaymentStatus(23_800m, Now);

        Assert.Equal(
            new OrderPaymentStatusChange(
                OrderPaymentStatus.FullPaymentReceived, OrderPaymentStatus.PartialPaymentReceived, 23_800m, 47_600m),
            change);
        Assert.Equal(OrderPaymentStatus.PartialPaymentReceived, order.PaymentStatus);
        Assert.Null(order.PaidMarkedAt);
        Assert.Null(order.PaidMarkedBy);
    }

    [Fact]
    public void AnEditThatDoesNotRaiseTheNetKeepsAPaidOrderWithPartialProofs()
    {
        var order = NewOrder(Proofs(20_000m));
        order.MarkPaid(Advisor, requirePaymentProof: false, Now);

        var change = order.RecalculatePaymentStatus(23_800m, Now);

        Assert.Equal(OrderPaymentStatus.FullPaymentReceived, order.PaymentStatus);
        Assert.Null(change);
    }

    [Fact]
    public void AnEditThatRaisesTheNetStillCoveredKeepsItPaid()
    {
        var order = NewOrder(Proofs(100_000m));
        order.MarkPaid(Advisor, requirePaymentProof: false, Now);
        AddSameLine(order);

        order.RecalculatePaymentStatus(23_800m, Now);

        Assert.Equal(OrderPaymentStatus.FullPaymentReceived, order.PaymentStatus);
    }

    [Fact]
    public void APaidOrderWithoutProofsStaysPaidAfterAnyEdit()
    {
        var order = NewOrder([]);
        order.MarkPaid(Advisor, requirePaymentProof: false, Now);
        AddSameLine(order);

        order.RecalculatePaymentStatus(23_800m, Now);

        Assert.Equal(OrderPaymentStatus.FullPaymentReceived, order.PaymentStatus);
    }

    // Review Focus 4: la retención le deja al neto 37 centavos que la pantalla no muestra.
    [Fact]
    public void ACopNetWithCentsIsCoveredByTheDisplayedPesos()
    {
        var order = NewOrder(
            Proofs(116_529m), lines: [Line(quantity: 1m, unitPrice: 119_030m)], header: Header(withRetention: true));

        Assert.Equal(116_529.37m, order.NetTotal);
        Assert.Equal(OrderPaymentStatus.PendingVerification, order.PaymentStatus);
        Assert.Equal(0m, order.Balance);
    }

    // El alta manual precia los descuentos de grupo después de crear el agregado: el pago se compara contra el neto
    // definitivo, sin que eso cuente como edición.
    [Fact]
    public void ADraftComparesItsPaymentAgainstTheNetAfterGroupDiscounts()
    {
        var order = NewOrder(Proofs(20_000m));
        Assert.Equal(OrderPaymentStatus.PartialPaymentReceived, order.PaymentStatus);
        order.ApplyGroupDiscounts(
            new Dictionary<OrderItemId, QuotationItemDiscount>
            {
                [order.Items.Single().Id] = new(20m, QuotationDiscountOrigin.Own),
            },
            Now);

        order.RecalculateDraftPaymentStatus();

        Assert.Equal(OrderPaymentStatus.PendingVerification, order.PaymentStatus);
        Assert.Equal(1, order.Version);
    }

    [Fact]
    public void BalanceIsPositiveWhenMissingAndNegativeWhenOver()
    {
        Assert.Equal(3_800m, NewOrder(Proofs(20_000m)).Balance);
        Assert.Equal(-1_200m, NewOrder(Proofs(25_000m)).Balance);
    }

    [Fact]
    public void SyncFromThePlatformIsAcceptedOncePrepared()
    {
        var order = ExternalOrder();
        order.Prepare(Advisor, Now);

        order.SyncExternalPaymentStatus(OrderPaymentStatus.FullPaymentReceived, autoArchive: false, Now);

        Assert.Equal(OrderPaymentStatus.FullPaymentReceived, order.PaymentStatus);
        Assert.Equal(OrderStatus.Open, order.Status);
        Assert.Null(order.PaidMarkedAt);
    }

    // Una marca de pagado describe un pagado: si la plataforma lo saca de pagado, la marca se va con él.
    [Fact]
    public void SyncFromThePlatformOutOfPaidClearsThePaidMark()
    {
        var order = ExternalOrder();
        order.MarkPaid(Advisor, requirePaymentProof: false, Now);

        order.SyncExternalPaymentStatus(OrderPaymentStatus.PaymentPending, autoArchive: false, Now);

        Assert.Equal(OrderPaymentStatus.PaymentPending, order.PaymentStatus);
        Assert.Null(order.PaidMarkedBy);
        Assert.Null(order.PaidMarkedAt);
    }

    [Fact]
    public void SyncFromThePlatformArchivesAPreparedOrderWhenTheTenantAutoArchives()
    {
        var order = ExternalOrder();
        order.Prepare(Advisor, Now);

        var archived = order.SyncExternalPaymentStatus(OrderPaymentStatus.FullPaymentReceived, autoArchive: true, Now);

        Assert.True(archived);
        Assert.Equal(OrderStatus.Archived, order.Status);
        Assert.Null(order.ArchivedBy);
    }

    [Fact]
    public void SyncFromThePlatformDoesNotArchiveAnUnpreparedOrder()
    {
        var order = ExternalOrder();

        var archived = order.SyncExternalPaymentStatus(OrderPaymentStatus.FullPaymentReceived, autoArchive: true, Now);

        Assert.False(archived);
        Assert.Equal(OrderStatus.Open, order.Status);
    }

    [Fact]
    public void SyncFromThePlatformIsAcceptedOnAnArchivedOrder()
    {
        var order = ExternalOrder();
        order.Archive(Advisor, Now);

        order.SyncExternalPaymentStatus(OrderPaymentStatus.FullPaymentReceived, autoArchive: true, Now);

        Assert.Equal(OrderPaymentStatus.FullPaymentReceived, order.PaymentStatus);
        Assert.Equal(OrderStatus.Archived, order.Status);
    }

    [Fact]
    public void SyncFromThePlatformIsRefusedOnceInvoiced()
    {
        var order = ExternalOrder();
        order.Invoice(Advisor, requireFullPayment: false, Now);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.SyncExternalPaymentStatus(OrderPaymentStatus.FullPaymentReceived, autoArchive: false, Now));

        Assert.Equal("order.order.not_editable", error.Code);
    }

    [Fact]
    public void SyncFromThePlatformIsRefusedOnceCancelled()
    {
        var order = ExternalOrder();
        order.CancelFromPlatform(Advisor, "Anulado en Shopify", Now);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.SyncExternalPaymentStatus(OrderPaymentStatus.FullPaymentReceived, autoArchive: false, Now));

        Assert.Equal("order.order.not_editable", error.Code);
    }
}
