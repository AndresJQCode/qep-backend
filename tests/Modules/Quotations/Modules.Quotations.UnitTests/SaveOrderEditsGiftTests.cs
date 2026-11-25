using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>Spec 2026-11-23: el guardado atómico de «Editar pedido» concilia los regalos después de las líneas pagadas, deja su
/// historial y su auditoría, y un <c>Gifts</c> ausente no toca nada (el frontend viejo no los borra).</summary>
public sealed class SaveOrderEditsGiftTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid SubjectId = Guid.CreateVersion7();
    private static readonly MemberId Advisor = new(Guid.CreateVersion7());
    private static readonly Guid V1 = Guid.CreateVersion7();
    private static readonly Guid V2 = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    private static readonly QuotationBillingAccount Account = new()
    {
        CompanyId = Guid.CreateVersion7(),
        BankName = "Bancolombia",
        AccountNumber = "7542",
        Currency = "COP",
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static QuotationVariantPricingRef Variant(Guid variantId, decimal listPrice) =>
        new(variantId, Guid.CreateVersion7(), TenantId, "Aceite", "ACE", true,
            new Dictionary<string, decimal> { ["COP"] = listPrice }, [], 0);

    // Una línea pagada de V1 x10 a 1.000 y un regalo de V2 x1, con la foto del producto en las dos.
    private static Order NewOrderWithAGift()
    {
        var order = Order.CreateManual(
            OrderId.New(), TenantId, "PED-2026-0001",
            new OrderHeader(Guid.CreateVersion7(), Advisor, "COP", null, false, null, false, []),
            QuotationParties.Empty, Account, ShippingDetails.None,
            [new OrderLineInput(V1, Guid.CreateVersion7(), 10m, 1_000m, 0m, 0, new QuotationProductSnapshot("ACE", "Aceite"))],
            null, Advisor, [], Now, orderDate: DateOnly.FromDateTime(Now.UtcDateTime));
        order.AddGift(
            OrderItemId.New(), V2, Guid.CreateVersion7(), 1m, 2_000m, 0, OrderLineCosts.None, Advisor, Now);
        order.CaptureProductSnapshots(new Dictionary<Guid, QuotationProductSnapshot>
        {
            [V1] = new("ACE", "Aceite"),
            [V2] = new("ACE", "Aceite"),
        });
        return order;
    }

    private sealed record Harness(
        StubOrderListRepository Orders, RecordingQuotationAuditPublisher Audit, SaveOrderEditsHandler Handler);

    private static Harness NewHarness(Order order)
    {
        var orders = new StubOrderListRepository(order);
        var audit = new RecordingQuotationAuditPublisher();
        var settings = new FixedQuotationSettingsStore(new QuotationSettings(1, new Dictionary<string, decimal>()));
        var catalog = new[] { Variant(V1, 1_000m), Variant(V2, 2_000m) };
        var handler = new SaveOrderEditsHandler(
            orders,
            new StubQuotationListRepository(),
            new NoOpQuotationsUnitOfWork(),
            audit,
            new StubQuotationProductPricingLookup(catalog),
            settings,
            new FixedTenantPriceRounding(),
            new StubQuotationProductLookup(catalog.ToDictionary(
                product => product.VariantId,
                product => new QuotationVariantRef(
                    product.VariantId, product.ProductId, product.Name, product.Code, null, product.Scales))),
            new CountingFileLookup(),
            new RecordingPaymentProofPublisher(),
            new NoOpOrderPaymentProofEventPublisher(),
            new ShippingResolver(new StubCarrierRepository(), new StubQuotationTaxRateLookup(null), settings),
            new StubMembershipDirectory(Advisor.Value),
            new StubTenantDirectory(),
            new StubExecutionContext(SubjectId, TenantId),
            new FixedClock(Now.AddDays(1)),
            new SaveOrderEditsValidator(),
            new StubQuotationCompanyLookup(new Dictionary<Guid, QuotationCompanyRef>()),
            new StubQuotationWithholdingLookup(),
            new FixedTenantClock(Now.AddDays(1)),
            new RecordingOrderVariantCostLookup(),
            new StubQuotationGeographyLookup(new Dictionary<Guid, string>()),
            new StubWholesaleLinePricing(),
            new StubQuotationCustomerLookup(
                new QuotationCustomerRef(Guid.CreateVersion7(), TenantId, "C-1", true, "Cliente", null, null, false, false)),
            // Los cuerpos de estas pruebas no traen entrega: Shopify no se pregunta.
            deliveryCheck: null!,
            lifecycleEvents: new RecordingQuotationLifecycleEventPublisher());
        return new Harness(orders, audit, handler);
    }

    private static SaveOrderEditsCommand Command(Order order, IReadOnlyList<OrderItemAddition>? gifts) =>
        new(
            TenantId,
            order.Id.Value,
            order.Version,
            [new OrderItemAddition(V1, 10m)],
            OrderEditProofs.None,
            Notes: null,
            GlobalScaleFloor: null,
            IsRetail: false,
            Gifts: gifts);

    [Fact]
    public async Task PutWithGiftsWritesGiftHistoryAndAudit()
    {
        var order = NewOrderWithAGift();
        var harness = NewHarness(order);

        var detail = await harness.Handler.HandleAsync(Command(order, [new OrderItemAddition(V1, 2m)]), Ct);

        var gift = Assert.Single(order.Items, item => item.IsGift);
        Assert.Equal(V1, gift.VariantId);
        Assert.Equal(2m, gift.Quantity);
        Assert.Single(detail.Order.Items, line => line.IsGift);
        Assert.Equal(
            ["Agregó de regalo Aceite x2.", "Quitó el regalo Aceite."],
            harness.Orders.History.Select(entry => entry.Details));
        Assert.All(harness.Orders.History, entry => Assert.Equal(OrderHistoryEventType.ItemsChanged, entry.EventType));
        Assert.Equal(["quotation.order.gift_added", "quotation.order.gift_removed"], harness.Audit.Actions);

        // El mismo regalo con otra cantidad: se cambia, no se quita y se vuelve a agregar.
        await harness.Handler.HandleAsync(Command(order, [new OrderItemAddition(V1, 3m)]), Ct);

        Assert.Equal(3m, Assert.Single(order.Items, item => item.IsGift).Quantity);
        Assert.Equal(3, harness.Orders.History.Count);
        Assert.Equal("Cambió la cantidad del regalo Aceite: 2 → 3.", harness.Orders.History[2].Details);
        Assert.Equal(
            ["quotation.order.gift_added", "quotation.order.gift_removed", "quotation.order.gift_updated"],
            harness.Audit.Actions);
    }

    [Fact]
    public async Task PutWithoutGiftsKeepsThemAndChangesNothing()
    {
        var order = NewOrderWithAGift();
        var harness = NewHarness(order);

        await harness.Handler.HandleAsync(Command(order, null), Ct);

        var gift = Assert.Single(order.Items, item => item.IsGift);
        Assert.Equal(V2, gift.VariantId);
        Assert.Empty(harness.Orders.History);
        Assert.Empty(harness.Audit.Actions);
    }

    [Fact]
    public async Task PutWithTheSameGiftsChangesNothing()
    {
        var order = NewOrderWithAGift();
        var harness = NewHarness(order);

        await harness.Handler.HandleAsync(Command(order, [new OrderItemAddition(V2, 1m)]), Ct);

        Assert.Equal(1m, Assert.Single(order.Items, item => item.IsGift).Quantity);
        Assert.Empty(harness.Orders.History);
        Assert.Empty(harness.Audit.Actions);
    }

    [Fact]
    public async Task PutWithGiftsOnANonEditableOrderIsRejected()
    {
        var order = NewOrderWithAGift();
        order.MarkPaid(Advisor, requirePaymentProof: false, order.WithoutCosts(), Now);
        order.Cancel(Advisor, "Cliente desistió", Now);
        var harness = NewHarness(order);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            harness.Handler.HandleAsync(Command(order, [new OrderItemAddition(V1, 2m)]), Ct));

        Assert.Equal("order.order.not_editable", error.Code);
        Assert.Equal(V2, Assert.Single(order.Items, item => item.IsGift).VariantId);
        Assert.Empty(harness.Orders.History);
    }

    private sealed class NoOpOrderPaymentProofEventPublisher : IOrderPaymentProofEventPublisher
    {
        public void PublishAttached(
            Guid tenantId, OrderId orderId, IReadOnlyCollection<AttachedPaymentProof> proofs, DateTimeOffset occurredAt)
        {
        }

        public void PublishDetached(
            Guid tenantId, OrderId orderId, IReadOnlyCollection<DetachedPaymentProof> proofs, DateTimeOffset occurredAt)
        {
        }
    }
}
