using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using static Modules.Quotations.UnitTests.WholesaleOrderFixtures;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Fase 3 (Task 5): el pedido que crea en el mayorista la orden de compra vinculada. Precio dado (la lista del distribuidor,
/// D-5), sin escalas; idempotente por la clave que incluye al comprador (X1, X8); la anulación del comprador escribe los
/// dos eventos. La transacción real la ejerce la integración.
/// </summary>
public sealed class WholesaleOrderGatewayTests
{
    private static readonly Guid CompanyId = Guid.CreateVersion7();
    private static readonly Guid VariantId = Guid.CreateVersion7();
    private static readonly Guid OtherVariantId = Guid.CreateVersion7();

    private readonly RecordingQuotationLifecycleEventPublisher _events = new();
    private readonly RecordingQuotationAuditPublisher _audit = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WholesaleOrderGateway Gateway(StubOrderListRepository orders)
    {
        var pricing = new StubQuotationProductPricingLookup(Variant(VariantId, 10_000m), Variant(OtherVariantId, 20_000m));
        var products = new StubQuotationProductLookup(new Dictionary<Guid, QuotationVariantRef>
        {
            [VariantId] = new(VariantId, VariantId, "Aceite", "SKU-1", null, []),
            [OtherVariantId] = new(OtherVariantId, OtherVariantId, "Aceite", "SKU-2", null, []),
        });
        var settings = new FixedQuotationSettingsStore();
        var rounding = new FixedTenantPriceRounding();
        var customers = new StubQuotationCustomerLookup(new QuotationCustomerRef(
            ClientId, TenantId, "CUC-001", IsActive: true, "Distribuciones Norte SAS", "3001234567", "Calle 1 # 2-3",
            WithRetention: false, VatSurplus: false));
        var companies = new StubQuotationCompanyLookup(new Dictionary<Guid, QuotationCompanyRef>
        {
            [CompanyId] = new(
                CompanyId, "Mayorista Central SAS", "900111222", "NIT", "CO", IsActive: true, null, null,
                [new QuotationCompanyAccountRef("Bancolombia", "7542", "USD"), new QuotationCompanyAccountRef("Davivienda", "1234", "COP")]),
        });
        var withholdings = new StubQuotationWithholdingLookup();
        var clock = new FixedTenantClock(Now);
        var drafts = new OrderDraftBuilder(
            customers, companies, pricing, products, settings, rounding,
            new ShippingResolver(new StubCarrierRepository(), new StubQuotationTaxRateLookup(null), settings),
            new FixedTenantDefaultCurrency(), new StubTenantDirectory(), withholdings, clock,
            new StubQuotationGeographyLookup(new Dictionary<Guid, string>()));
        return new WholesaleOrderGateway(
            drafts, pricing, settings, rounding, customers, companies, orders, new NoOpQuotationsUnitOfWork(), _audit, _events,
            new SequentialOrderNumberGenerator(), new DefaultNumberingFormats(), clock, withholdings);
    }

    private static WholesaleOrderDraft Draft(params WholesaleOrderLine[] lines) =>
        new(
            TenantId, BuyerTenantId, PurchaseOrderId, PurchaseOrderNumber, ClientId, Advisor.Value, CompanyId, "COP",
            "Entregar en bodega", lines.Length == 0 ? [new WholesaleOrderLine(Guid.CreateVersion7(), VariantId, 6m, 8_500m)] : lines);

    [Fact]
    public async Task CreateUsesTheGivenPricesWithoutScales()
    {
        var orders = new StubOrderListRepository();

        await Gateway(orders).CreateAsync(Draft(), Ct);

        var item = Assert.Single(Assert.Single(orders.Added).Items);
        // La escala del 10 % desde 5 unidades no aplica: 6 u. al precio de lista dado.
        Assert.Equal((6m, 8_500m, 0m), (item.Quantity, item.UnitPrice, item.DiscountPercentage));
        Assert.Equal(19, item.TaxPercentage);
    }

    [Fact]
    public async Task CreateStoresTheSourceLineIdsAndThePurchaseOrderNumber()
    {
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var orders = new StubOrderListRepository();

        var result = await Gateway(orders).CreateAsync(
            Draft(new(first, VariantId, 1m, 8_500m), new(second, OtherVariantId, 2m, 17_000m)), Ct);

        var order = Assert.Single(orders.Added);
        Assert.Equal(OrderSource.Wholesale, order.Source);
        Assert.Equal(PurchaseOrderNumber, order.ExternalReference);
        Assert.Equal((BuyerTenantId, PurchaseOrderId), (order.BuyerTenantId, order.PurchaseOrderId));
        Assert.Equal(Order.WholesaleExternalKey(BuyerTenantId, PurchaseOrderId), order.ExternalKey);
        Assert.Equal("Entregar en bodega", order.BuyerNotes);
        Assert.False(order.IsRetail);
        // La cuenta de la empresa en la moneda del mayorista, no la primera que tenga.
        Assert.Equal(("Davivienda", "COP"), (order.BillingAccount!.BankName, order.BillingAccount.Currency));
        Assert.False(result.AlreadyExisted);
        Assert.Equal([(first, VariantId, 1m, 8_500m, 19m), (second, OtherVariantId, 2m, 17_000m, 19m)],
            result.Lines.Select(line => (line.PurchaseOrderLineId, line.VariantId, line.Quantity, line.UnitPrice, line.TaxPercentage)));
        Assert.All(result.Lines, line => Assert.Equal("Aceite", line.Name));
    }

    [Fact]
    public async Task CreateWithATakenKeyReturnsTheExistingOrder()
    {
        var keptLine = Guid.CreateVersion7();
        var existing = Wholesale(Line(VariantId, 3m, 9_000m, keptLine));
        var orders = new StubOrderListRepository(existing);

        var result = await Gateway(orders).CreateAsync(Draft(), Ct);

        Assert.True(result.AlreadyExisted);
        Assert.Equal(existing.Id.Value, result.OrderId);
        Assert.Empty(orders.Added);
        var line = Assert.Single(result.Lines);
        Assert.Equal((keptLine, 3m, 9_000m), (line.PurchaseOrderLineId, line.Quantity, line.UnitPrice));
        Assert.Empty(_events.Published);
    }

    [Fact]
    public async Task CreatePublishesOrderCreated()
    {
        var orders = new StubOrderListRepository();

        var result = await Gateway(orders).CreateAsync(Draft(), Ct);

        Assert.Equal([("order.created", result.OrderId)], _events.Published.Select(entry => (entry.Event, entry.QuotationId)));
        Assert.Contains("quotation.order.created", _audit.Actions);
        var history = Assert.Single(orders.History);
        Assert.Null(history.MemberId);
    }

    [Fact]
    public async Task CancelByBuyerPublishesBothCancellationEvents()
    {
        var order = Wholesale(Line(VariantId, 1m, 8_500m));
        var orders = new StubOrderListRepository(order);

        var cancelled = await Gateway(orders).CancelByBuyerAsync(
            TenantId, order.Id.Value, BuyerTenantId, PurchaseOrderId, PurchaseOrderNumber, Ct);

        Assert.True(cancelled);
        Assert.True(order.CancelledByBuyer);
        Assert.Equal("Cancelada por el distribuidor (OC-0007).", order.CancellationReason);
        Assert.Equal(["cancelled", "wholesale.cancelled"], _events.Published.Select(entry => entry.Event));
        Assert.Equal([(order.Id.Value, true)], _events.WholesaleCancelled);
        Assert.Equal([("quotation.order.cancelled", Guid.Empty)], _audit.Entries.Select(entry => (entry.Action, entry.ActorId)));
        Assert.Equal(OrderHistoryEventType.Cancelled, Assert.Single(orders.History).EventType);
    }

    [Fact]
    public async Task CancelByBuyerTwiceIsIdempotent()
    {
        var order = Wholesale(Line(VariantId, 1m, 8_500m));
        var orders = new StubOrderListRepository(order);
        var gateway = Gateway(orders);
        await gateway.CancelByBuyerAsync(TenantId, order.Id.Value, BuyerTenantId, PurchaseOrderId, PurchaseOrderNumber, Ct);

        var again = await gateway.CancelByBuyerAsync(
            TenantId, order.Id.Value, BuyerTenantId, PurchaseOrderId, PurchaseOrderNumber, Ct);

        Assert.False(again);
        Assert.Equal(2, _events.Published.Count);
    }

    [Fact]
    public async Task CancelByBuyerOfAnApprovedOrderThrowsNotCancellableByBuyer()
    {
        var order = Wholesale(Line(VariantId, 1m, 8_500m));
        order.ApproveWholesale(Advisor, Now);
        var orders = new StubOrderListRepository(order);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() => Gateway(orders).CancelByBuyerAsync(
            TenantId, order.Id.Value, BuyerTenantId, PurchaseOrderId, PurchaseOrderNumber, Ct));

        Assert.Equal(BuyerOrderErrors.NotCancellableByBuyer, error.Code);
        Assert.Equal(OrderStatus.Open, order.Status);
        Assert.Empty(_events.Published);
    }
}
