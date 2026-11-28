using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Los eventos que el inventario básico del WMS consume de los pedidos (<c>edited</c>, <c>prepared</c>, <c>unprepared</c>,
/// <c>archived</c> y <c>unarchived</c>): cada camino que cambia lo que el WMS necesita saber publica el suyo, <b>justo después de
/// su mutación</b>, para que dos eventos del mismo caso de uso lleven versiones distintas y crecientes.
/// </summary>
public sealed class OrderStockEventsTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid SubjectId = Guid.CreateVersion7();
    private static readonly MemberId Advisor = new(Guid.CreateVersion7());
    private static readonly Guid V1 = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

    private static readonly QuotationBillingAccount Account = new()
    {
        CompanyId = Guid.CreateVersion7(),
        BankName = "Bancolombia",
        AccountNumber = "7542",
        Currency = "COP",
        Country = "CO",
    };

    private static readonly OrderSettings AutoArchiving = new(false, true, true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly RecordingQuotationLifecycleEventPublisher _events = new();
    private readonly RecordingQuotationAuditPublisher _audit = new();

    private static QuotationVariantPricingRef Variant(Guid variantId, decimal listPrice) =>
        new(variantId, Guid.CreateVersion7(), TenantId, "Aceite", "ACE", true,
            new Dictionary<string, decimal> { ["COP"] = listPrice }, [], 0);

    private static OrderHeader Header() => new(Guid.CreateVersion7(), Advisor, "COP", null, false, null, false, []);

    // Un pedido manual abierto, con una línea de V1 por 10 a 1.000 y la foto del producto.
    private static Order ManualOrder() =>
        Order.CreateManual(
            OrderId.New(), TenantId, "PED-2026-0001", Header(), QuotationParties.Empty, Account, ShippingDetails.None,
            [new OrderLineInput(V1, Guid.CreateVersion7(), 10m, 1_000m, 0m, 0, new QuotationProductSnapshot("ACE", "Aceite"))],
            null, Advisor, [], Now, orderDate: DateOnly.FromDateTime(Now.UtcDateTime));

    // Un pedido de Shopify abierto, con una línea de V1 por 10 a 1.000, pago pendiente.
    private static Order ShopifyOrder() =>
        Order.CreateExternal(
            OrderId.New(), TenantId, "PED-2026-0002", Header(), QuotationParties.Empty, Account,
            [new OrderLineInput(V1, Guid.CreateVersion7(), 10m, 1_000m, 0m, 0, new QuotationProductSnapshot("ACE", "Aceite"))],
            OrderPaymentStatus.PaymentPending, null, Advisor, [], Now, DateOnly.FromDateTime(Now.UtcDateTime),
            OrderSource.Shopify, "#1001", ShippingDetails.None, null, null, OrderLineCosts.None);

    private static void Pay(Order order) =>
        order.MarkPaid(Advisor, false, OrderLineCosts.Of(order.UncostedVariantIds, new Dictionary<Guid, decimal>()), Now);

    private PrepareOrderHandler Prepare(Order order, OrderSettings? settings = null) =>
        new(new StubOrderListRepository(order), new NoOpQuotationsUnitOfWork(), _audit, new StubMembershipDirectory(Advisor.Value),
            new StubExecutionContext(SubjectId, TenantId), new FixedClock(Now.AddMinutes(1)),
            new FixedQuotationSettingsStore { OrderSettings = settings ?? OrderSettings.Default }, _events);

    private async Task<Order> PrepareAsync(Order order, OrderSettings? settings = null)
    {
        await Prepare(order, settings).HandleAsync(new PrepareOrderCommand(TenantId, order.Id.Value), Ct);
        _events.StockEvents.Clear();
        return order;
    }

    [Fact]
    public async Task SavingEditsPublishesEdited()
    {
        var order = ManualOrder();
        var settings = new FixedQuotationSettingsStore(new QuotationSettings(1, new Dictionary<string, decimal>()));
        var catalog = new[] { Variant(V1, 1_000m) };
        var handler = new SaveOrderEditsHandler(
            new StubOrderListRepository(order),
            new StubQuotationListRepository(),
            new NoOpQuotationsUnitOfWork(),
            _audit,
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
            deliveryCheck: null!,
            lifecycleEvents: _events);

        await handler.HandleAsync(
            new SaveOrderEditsCommand(
                TenantId, order.Id.Value, order.Version, [new OrderItemAddition(V1, 12m)], OrderEditProofs.None,
                Notes: null, GlobalScaleFloor: null, IsRetail: false),
            Ct);

        var published = Assert.Single(_events.StockEvents);
        Assert.Equal(("edited", order.Items.Single().Quantity), (published.Name, 12m));
        Assert.Equal(1, published.Lines);
        Assert.True(published.Version > 1);
    }

    [Fact]
    public async Task AddingItemsPublishesEdited()
    {
        var order = ManualOrder();
        var added = Guid.CreateVersion7();
        var catalog = new[] { Variant(V1, 1_000m), Variant(added, 2_000m) };
        var settings = new FixedQuotationSettingsStore(new QuotationSettings(1, new Dictionary<string, decimal>()));
        var handler = new AddOrderItemsHandler(
            new StubOrderListRepository(order),
            new StubQuotationListRepository(),
            new NoOpQuotationsUnitOfWork(),
            _audit,
            new StubQuotationProductPricingLookup(catalog),
            settings,
            new FixedTenantPriceRounding(),
            new StubQuotationProductLookup(catalog.ToDictionary(
                product => product.VariantId,
                product => new QuotationVariantRef(
                    product.VariantId, product.ProductId, product.Name, product.Code, null, product.Scales))),
            new StubMembershipDirectory(Advisor.Value),
            new StubTenantDirectory(),
            new StubExecutionContext(SubjectId, TenantId),
            new FixedClock(Now.AddMinutes(1)),
            new AddOrderItemsValidator(),
            new RecordingOrderVariantCostLookup(),
            new StubWholesaleLinePricing(),
            _events);

        await handler.HandleAsync(new AddOrderItemsCommand(TenantId, order.Id.Value, [new OrderItemAddition(added, 3m)]), Ct);

        var published = Assert.Single(_events.StockEvents);
        Assert.Equal(("edited", 2), (published.Name, published.Lines));
        Assert.True(published.Version > 1);
    }

    private ExternalOrderService ExternalWriter(Order order, OrderSettings? settings = null) =>
        new(
            new StubOrderListRepository(order),
            new NoOpQuotationsUnitOfWork(),
            _audit,
            new StubQuotationCustomerLookup(
                new QuotationCustomerRef(Guid.CreateVersion7(), TenantId, "C-1", true, "Cliente", null, null, false, false)),
            new StubQuotationCompanyLookup(new Dictionary<Guid, QuotationCompanyRef>()),
            new StubQuotationProductLookup(new Dictionary<Guid, QuotationVariantRef>()),
            _events,
            new SequentialOrderNumberGenerator(),
            new DefaultNumberingFormats(),
            new FixedTenantClock(Now.AddMinutes(1)),
            new StubQuotationWithholdingLookup(),
            new FixedQuotationSettingsStore { OrderSettings = settings ?? OrderSettings.Default },
            new RecordingOrderVariantCostLookup());

    private static ExternalOrderEditDraft EditDraft(Order order, IReadOnlyList<ExternalOrderLine>? lines, string? buyerNotes = null) =>
        new(
            TenantId, order.Id.Value, order.Version, OrderSource.Shopify, lines, null, null,
            ReplaceBuyerNotes: buyerNotes is not null, buyerNotes, "Shopify", Now.AddMinutes(1));

    [Fact]
    public async Task AnExternalEditPublishesEdited()
    {
        var order = ShopifyOrder();

        await ExternalWriter(order).ApplyEditAsync(
            EditDraft(order, [new ExternalOrderLine(V1, Guid.CreateVersion7(), 4m, 1_000m, 0m, 0)]), Ct);

        var published = Assert.Single(_events.StockEvents);
        Assert.Equal(("edited", 1), (published.Name, published.Lines));
        Assert.Equal(order.Version, published.Version);
        Assert.Equal(4m, order.Items.Single().Quantity);
    }

    [Fact]
    public async Task AnExternalEditThatChangesNothingPublishesNothing()
    {
        var order = ShopifyOrder();
        var line = order.Items.Single();

        await ExternalWriter(order).ApplyEditAsync(
            EditDraft(order, [new ExternalOrderLine(line.VariantId, line.ProductId, line.Quantity, line.UnitPrice, line.DiscountPercentage, line.ProductTaxRate)]),
            Ct);

        Assert.Empty(_events.StockEvents);
    }

    [Fact]
    public async Task AnExternalEditOfOnlyTheBuyerNotesPublishesNothing()
    {
        var order = ShopifyOrder();

        await ExternalWriter(order).ApplyEditAsync(EditDraft(order, null, buyerNotes: "Llamar antes de entregar"), Ct);

        Assert.Empty(_events.StockEvents);
    }

    [Fact]
    public async Task PreparingPublishesPreparedWithManualOrigin()
    {
        var order = ManualOrder();

        await Prepare(order).HandleAsync(new PrepareOrderCommand(TenantId, order.Id.Value), Ct);

        var published = Assert.Single(_events.StockEvents);
        Assert.Equal(("prepared", OrderEventOrigins.Manual, 1), (published.Name, published.Origin, published.Lines));
        Assert.Equal(order.Version, published.Version);
        Assert.True(order.IsPrepared);
    }

    [Fact]
    public async Task PreparingAPaidOrderPublishesPreparedThenArchivedWithIncreasingVersions()
    {
        var order = ManualOrder();
        Pay(order);

        await Prepare(order, AutoArchiving).HandleAsync(new PrepareOrderCommand(TenantId, order.Id.Value), Ct);

        Assert.Equal(["prepared", "archived"], _events.StockEvents.Select(entry => entry.Name));
        Assert.True(_events.StockEvents[1].Version > _events.StockEvents[0].Version);
        Assert.Equal(order.Version, _events.StockEvents[1].Version);
        Assert.True(_events.StockEvents[1].IsPrepared);
        Assert.Equal(OrderStatus.Archived, order.Status);
    }

    [Fact]
    public async Task UndoingPreparationPublishesUnprepared()
    {
        var order = await PrepareAsync(ManualOrder());
        var handler = new UndoOrderPreparationHandler(
            new StubOrderListRepository(order), new NoOpQuotationsUnitOfWork(), _audit, new StubMembershipDirectory(Advisor.Value),
            new StubExecutionContext(SubjectId, TenantId), new FixedClock(Now.AddMinutes(2)), _events);

        await handler.HandleAsync(new UndoOrderPreparationCommand(TenantId, order.Id.Value), Ct);

        var published = Assert.Single(_events.StockEvents);
        Assert.Equal("unprepared", published.Name);
        Assert.Equal(order.Version, published.Version);
        Assert.False(order.IsPrepared);
    }

    private ArchiveOrderHandler Archiver(Order order) =>
        new(new StubOrderListRepository(order), new NoOpQuotationsUnitOfWork(), _audit, new StubMembershipDirectory(Advisor.Value),
            new StubExecutionContext(SubjectId, TenantId), new FixedClock(Now.AddMinutes(3)), _events);

    private UnarchiveOrderHandler Unarchiver(Order order) =>
        new(new StubOrderListRepository(order), new NoOpQuotationsUnitOfWork(), _audit, new StubMembershipDirectory(Advisor.Value),
            new StubExecutionContext(SubjectId, TenantId), new FixedClock(Now.AddMinutes(4)), _events);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ArchivingPublishesIsPrepared(bool prepared)
    {
        var order = ManualOrder();
        if (prepared)
        {
            await PrepareAsync(order);
        }

        await Archiver(order).HandleAsync(new ArchiveOrderCommand(TenantId, order.Id.Value), Ct);

        var published = Assert.Single(_events.StockEvents);
        Assert.Equal(("archived", prepared, 1), (published.Name, published.IsPrepared, published.Lines));
        Assert.Equal(order.Version, published.Version);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnarchivingCarriesTheSameIsPrepared(bool prepared)
    {
        var order = ManualOrder();
        if (prepared)
        {
            await PrepareAsync(order);
        }

        await Archiver(order).HandleAsync(new ArchiveOrderCommand(TenantId, order.Id.Value), Ct);
        await Unarchiver(order).HandleAsync(new UnarchiveOrderCommand(TenantId, order.Id.Value), Ct);

        Assert.Equal(["archived", "unarchived"], _events.StockEvents.Select(entry => entry.Name));
        Assert.Equal(prepared, _events.StockEvents[0].IsPrepared);
        Assert.Equal(_events.StockEvents[0].IsPrepared, _events.StockEvents[1].IsPrepared);
        Assert.True(_events.StockEvents[1].Version > _events.StockEvents[0].Version);
    }

    [Fact]
    public async Task MarkingPaidThatAutoArchivesPublishesArchived()
    {
        var order = await PrepareAsync(ManualOrder());
        var handler = new MarkOrderPaidHandler(
            new StubOrderListRepository(order), new NoOpQuotationsUnitOfWork(), _audit, new StubMembershipDirectory(Advisor.Value),
            new StubExecutionContext(SubjectId, TenantId), new FixedClock(Now.AddMinutes(5)), new StubTenantDirectory(),
            new StubQuotationCustomerLookup(new QuotationCustomerRef(
                Guid.CreateVersion7(), TenantId, "CUC-001", IsActive: true, "Cliente", "3001234567", "Calle 1 # 2-3",
                WithRetention: false, VatSurplus: false, PersonType: "Natural", Country: "CO",
                IdentificationNumber: "900123456", IdentificationType: "NIT")),
            new FixedQuotationSettingsStore { OrderSettings = AutoArchiving },
            new RecordingOrderVariantCostLookup(),
            _events);

        await handler.HandleAsync(new MarkOrderPaidCommand(TenantId, order.Id.Value), Ct);

        var published = Assert.Single(_events.StockEvents);
        Assert.Equal(("archived", true), (published.Name, published.IsPrepared));
        Assert.Equal(order.Version, published.Version);
        Assert.Equal(OrderStatus.Archived, order.Status);
    }

    [Fact]
    public async Task APaymentSyncThatArchivesPublishesArchived()
    {
        var order = ShopifyOrder();
        order.Prepare(Advisor, Now);

        await ExternalWriter(order, AutoArchiving).SyncPaymentStatusAsync(
            TenantId, order.Id.Value, OrderPaymentStatus.FullPaymentReceived, Ct);

        var published = Assert.Single(_events.StockEvents);
        Assert.Equal(("archived", true), (published.Name, published.IsPrepared));
        Assert.Equal(order.Version, published.Version);
    }

    [Fact]
    public async Task APaymentSyncThatDoesNotArchivePublishesNothing()
    {
        var order = ShopifyOrder();

        await ExternalWriter(order, AutoArchiving).SyncPaymentStatusAsync(
            TenantId, order.Id.Value, OrderPaymentStatus.FullPaymentReceived, Ct);

        Assert.Empty(_events.StockEvents);
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
