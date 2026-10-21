using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>La entrega y el envío del pedido (spec 2026-10-17 flete §5.2, §5.4 y §6).</summary>
public sealed class OrderShippingTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly MemberId Advisor = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid CarrierId = Guid.CreateVersion7();

    private static QuotationBillingAccount Account => new()
    {
        CompanyId = Guid.CreateVersion7(),
        BankName = "Bancolombia",
        AccountNumber = "7542",
        Currency = "COP",
    };

    private static OrderHeader Header => new(ClientId, Advisor, "COP", null, false, null, false, false);

    private static OrderLineInput Line =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), 10m, 11_900m, 0m, 19, new QuotationProductSnapshot("ABC", "Aceite"));

    private static Order NewOrder(
        ShippingDetails shipping,
        OrderPaymentStatus paymentStatus = OrderPaymentStatus.PaymentPending,
        IReadOnlyCollection<OrderPaymentProofInput>? proofs = null,
        bool isStorePickup = false) =>
        Order.CreateManual(
            OrderId.New(), TenantId, "PED-2026-0001", Header, new QuotationParties(null, null, IsStorePickup: isStorePickup),
            Account, shipping, [Line], paymentStatus, null, Advisor, proofs ?? [], Now);

    [Fact]
    public void FromQuotationCopiesTheShippingAndTheTotals()
    {
        var quotation = Quotation.Create(
            QuotationId.New(), TenantId, "COT-2026-0001", ClientId, Advisor, new DateOnly(2026, 11, 1), null, null,
            QuotationParties.Empty, Account, "COP", customerWithRetention: true, customerVatSurplus: false, Advisor, Now);
        quotation.AddItem(QuotationItemId.New(), Guid.CreateVersion7(), Guid.CreateVersion7(), 10m, 11_900m, 0m, 19, Advisor, Now);
        var shipping = new ShippingDetails(CarrierId, "Coordinadora", FreightChargeMode.Charged, 15_000m, 19, true);
        quotation.SetShipping(shipping, Advisor, Now);
        quotation.ConvertToOrder(Advisor, Now, new Dictionary<Guid, QuotationProductSnapshot>());

        var order = Order.FromQuotation(
            OrderId.New(), TenantId, "PED-2026-0001", quotation, OrderPaymentStatus.PaymentPending, null, Advisor, [], Now);

        Assert.Equal(shipping, order.ShippingDetails);
        Assert.Equal(
            (quotation.FreightSubtotal, quotation.FreightTaxAmount, quotation.Total, quotation.RetentionAmount, quotation.NetTotal),
            (order.FreightSubtotal, order.FreightTaxAmount, order.Total, order.RetentionAmount, order.NetTotal));
        Assert.Equal(134_000m, order.Total);
    }

    [Fact]
    public void FromQuotationWithCashOnDeliveryMustBePending()
    {
        var quotation = Quotation.Create(
            QuotationId.New(), TenantId, "COT-2026-0001", ClientId, Advisor, new DateOnly(2026, 11, 1), null, null,
            QuotationParties.Empty, Account, "COP", customerWithRetention: false, customerVatSurplus: false, Advisor, Now);
        quotation.AddItem(QuotationItemId.New(), Guid.CreateVersion7(), Guid.CreateVersion7(), 10m, 11_900m, 0m, 19, Advisor, Now);
        quotation.SetShipping(new ShippingDetails(null, null, null, 0m, 0, true), Advisor, Now);
        quotation.ConvertToOrder(Advisor, Now, new Dictionary<Guid, QuotationProductSnapshot>());

        var error = Assert.Throws<QuotationsDomainException>(() => Order.FromQuotation(
            OrderId.New(), TenantId, "PED-2026-0001", quotation, OrderPaymentStatus.FullPaymentReceived, null, Advisor,
            [new OrderPaymentProofInput(Guid.CreateVersion7(), 119_000m)], Now));

        Assert.Equal("order.order.cash_on_delivery_pending_only", error.Code);
    }

    [Fact]
    public void CreateManualCarriesTheShippingIntoTheTotals()
    {
        var order = NewOrder(new ShippingDetails(CarrierId, "Coordinadora", FreightChargeMode.Charged, 15_000m, 19, false));

        Assert.Equal((12_605.04m, 2_394.96m, 134_000m), (order.FreightSubtotal, order.FreightTaxAmount, order.Total));
        Assert.Equal((CarrierId, "Coordinadora"), (order.CarrierId, order.CarrierName));
    }

    [Fact]
    public void CreateManualWithCashOnDeliveryMustBePending()
    {
        var error = Assert.Throws<QuotationsDomainException>(() => NewOrder(
            new ShippingDetails(null, null, null, 0m, 0, true),
            OrderPaymentStatus.FullPaymentReceived,
            [new OrderPaymentProofInput(Guid.CreateVersion7(), 119_000m)]));

        Assert.Equal("order.order.cash_on_delivery_pending_only", error.Code);
    }

    // R16: la misma regla que SetShipping (D-F14). Pendiente de pago pero con un comprobante ya es un pago
    // registrado, y un pedido contraentrega se cobra en la puerta.
    [Fact]
    public void CreateManualWithCashOnDeliveryRejectsProofsEvenWhilePending()
    {
        var error = Assert.Throws<QuotationsDomainException>(() => NewOrder(
            new ShippingDetails(null, null, null, 0m, 0, true),
            OrderPaymentStatus.PaymentPending,
            [new OrderPaymentProofInput(Guid.CreateVersion7(), 10_000m)]));

        Assert.Equal("order.order.cash_on_delivery_pending_only", error.Code);
    }

    [Fact]
    public void FromQuotationWithCashOnDeliveryRejectsProofsEvenWhilePending()
    {
        var quotation = Quotation.Create(
            QuotationId.New(), TenantId, "COT-2026-0001", ClientId, Advisor, new DateOnly(2026, 11, 1), null, null,
            QuotationParties.Empty, Account, "COP", customerWithRetention: false, customerVatSurplus: false, Advisor, Now);
        quotation.AddItem(QuotationItemId.New(), Guid.CreateVersion7(), Guid.CreateVersion7(), 10m, 11_900m, 0m, 19, Advisor, Now);
        quotation.SetShipping(new ShippingDetails(null, null, null, 0m, 0, true), Advisor, Now);
        quotation.ConvertToOrder(Advisor, Now, new Dictionary<Guid, QuotationProductSnapshot>());

        var error = Assert.Throws<QuotationsDomainException>(() => Order.FromQuotation(
            OrderId.New(), TenantId, "PED-2026-0001", quotation, OrderPaymentStatus.PaymentPending, null, Advisor,
            [new OrderPaymentProofInput(Guid.CreateVersion7(), 10_000m)], Now));

        Assert.Equal("order.order.cash_on_delivery_pending_only", error.Code);
    }

    [Fact]
    public void CreateManualRejectsShippingOnPickup()
    {
        var error = Assert.Throws<QuotationsDomainException>(() => NewOrder(
            new ShippingDetails(null, null, FreightChargeMode.Free, 0m, 0, false), isStorePickup: true));

        Assert.Equal("order.order.shipping_not_allowed_on_pickup", error.Code);
    }

    [Fact]
    public void CreateManualRejectsChargedWithZero()
    {
        var error = Assert.Throws<QuotationsDomainException>(() => NewOrder(
            new ShippingDetails(null, null, FreightChargeMode.Charged, 0m, 19, false)));

        Assert.Equal("order.order.freight_amount_required", error.Code);
    }

    // Review Focus 5 (D-F14): un pedido con comprobantes ya se está pagando; no se cobra en la puerta.
    [Fact]
    public void CashOnDeliveryCannotBeTurnedOnWithProofs()
    {
        var order = NewOrder(
            ShippingDetails.None, OrderPaymentStatus.PaymentPending,
            [new OrderPaymentProofInput(Guid.CreateVersion7(), 10_000m)]);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.SetShipping(new ShippingDetails(null, null, null, 0m, 0, true), Advisor, Now));

        Assert.Equal("order.order.cash_on_delivery_pending_only", error.Code);
    }

    // Ya prendida, la contraentrega sobrevive al giro: el comprobante del recaudo no la apaga.
    [Fact]
    public void CashOnDeliveryAlreadyOnCanBeKeptWithProofs()
    {
        var order = NewOrder(new ShippingDetails(null, null, null, 0m, 0, true));
        order.AttachPaymentProofs([new OrderPaymentProofInput(Guid.CreateVersion7(), 119_000m)], Advisor, Now);

        order.SetShipping(new ShippingDetails(null, null, FreightChargeMode.Free, 0m, 0, true), Advisor, Now);

        Assert.Equal(FreightChargeMode.Free, order.FreightChargeMode);
    }

    [Fact]
    public void SetShippingRecalculatesTheTotals()
    {
        var order = NewOrder(ShippingDetails.None);
        var version = order.Version;

        order.SetShipping(new ShippingDetails(null, null, FreightChargeMode.Charged, 15_000m, 19, false), Advisor, Now);

        Assert.Equal(134_000m, order.Total);
        Assert.Equal(version + 1, order.Version);
    }

    [Fact]
    public void SetShippingOnlyWhilePending()
    {
        var order = NewOrder(ShippingDetails.None);
        order.Approve(Advisor, Now);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.SetShipping(ShippingDetails.None, Advisor, Now));

        Assert.Equal("order.order.not_pending", error.Code);
    }

    private static Order NewExternal(
        ShippingDetails shipping,
        OrderPaymentStatus paymentStatus = OrderPaymentStatus.FullPaymentReceived,
        bool isStorePickup = false,
        string? externalKey = null,
        string? externalShippingTitle = null) =>
        Order.CreateExternal(
            OrderId.New(), TenantId, "PED-2026-0002", Header, new QuotationParties(null, null, IsStorePickup: isStorePickup),
            Account, [Line], paymentStatus, null, Advisor, [], Now, OrderSource.Shopify, "#1001", shipping, externalKey,
            externalShippingTitle);

    [Fact]
    public void CreateExternalWithoutShippingHasNone()
    {
        var order = NewExternal(ShippingDetails.None);

        Assert.Equal(ShippingDetails.None, order.ShippingDetails);
        Assert.Equal((0m, 0m, 119_000m), (order.FreightSubtotal, order.FreightTaxAmount, order.Total));
    }

    // Spec de Shopify §6.2 y flete §10: el envío que cobró la tienda entra al pedido con la misma aritmética que
    // el manual. 15.000 con IVA del 19 % → 12.605,04 + 2.394,96.
    [Fact]
    public void CreateExternalWithChargedCopShippingAddsFreightToTotal()
    {
        var shipping = new ShippingDetails(null, null, FreightChargeMode.Charged, 15_000m, 19, false);

        var order = NewExternal(shipping);

        Assert.Equal(shipping, order.ShippingDetails);
        Assert.Equal(12_605.04m, order.FreightSubtotal);
        Assert.Equal(2_394.96m, order.FreightTaxAmount);
        Assert.Equal(order.ProductsTotal + 15_000m, order.Total);
        Assert.Equal(134_000m, order.Total);
    }

    [Fact]
    public void CreateExternalCashOnDeliveryWithPaidStatusThrows()
    {
        var shipping = new ShippingDetails(null, null, FreightChargeMode.Free, 0m, 19, true);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            NewExternal(shipping, OrderPaymentStatus.FullPaymentReceived));

        Assert.Equal("order.order.cash_on_delivery_pending_only", error.Code);
    }

    [Fact]
    public void CreateExternalStorePickupWithShippingThrows()
    {
        var shipping = new ShippingDetails(null, null, FreightChargeMode.Charged, 15_000m, 19, false);

        var error = Assert.Throws<QuotationsDomainException>(() => NewExternal(shipping, isStorePickup: true));

        Assert.Equal("order.order.shipping_not_allowed_on_pickup", error.Code);
    }

    [Fact]
    public void CreateExternalExternalKeyTooLongThrows()
    {
        var error = Assert.Throws<QuotationsDomainException>(() =>
            NewExternal(ShippingDetails.None, externalKey: new string('k', 129)));

        Assert.Equal("order.order.external_key_too_long", error.Code);
    }

    [Fact]
    public void CreateExternalNormalizesExternalKeyAndTruncatesShippingTitle()
    {
        var order = NewExternal(
            ShippingDetails.None, externalKey: "  shop-1:order-9  ", externalShippingTitle: "  " + new string('t', 130) + " ");

        Assert.Equal("shop-1:order-9", order.ExternalKey);
        Assert.Equal(new string('t', Order.ExternalShippingTitleMaxLength), order.ExternalShippingTitle);
    }

    [Fact]
    public void CreateExternalBlankExternalKeyAndTitleAreNull()
    {
        var order = NewExternal(ShippingDetails.None, externalKey: "  ", externalShippingTitle: " ");

        Assert.Equal(((string?)null, (string?)null), (order.ExternalKey, order.ExternalShippingTitle));
    }
}
