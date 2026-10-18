using BuildingBlocks.Application;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.UnitTests;

/// <summary>«Reconocer este nombre» (spec de Shopify D-S25): fijar la transportadora de un pedido externo
/// cuyo título de envío no se reconoció y, si se pide, recordar ese título en la transportadora.</summary>
public sealed class RecognizeOrderCarrierHandlerTests
{
    private const string Title = "Envío Coordinadora 24h";
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid SubjectId = Guid.CreateVersion7();
    private static readonly MemberId Advisor = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private static readonly ShippingDetails ChargedShipping =
        new(null, null, FreightChargeMode.Charged, 15_000m, 19, false);

    [Fact]
    public async Task RecognizeWithoutSettingsUpdateAndRememberThrows403()
    {
        var order = NewExternal(ChargedShipping);
        var carrier = NewCarrier();
        var carriers = new StubCarrierRepository(carrier);
        var modules = FixedTenantModules.Simulated;
        var audit = new RecordingQuotationAuditPublisher();
        var handler = NewHandler(
            order, carriers, audit,
            new StubExecutionContext(SubjectId, TenantId, TenancyPermissions.SettingsUpdate), modules);

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(Command(order, carrier, remember: true), TestContext.Current.CancellationToken));

        Assert.Equal("authorization.denied", error.Code);
        Assert.Equal(0, carriers.FindCalls);
        Assert.Equal(0, modules.FindCalls);
        Assert.Null(order.CarrierId);
        Assert.Empty(audit.Actions);
    }

    [Fact]
    public async Task RecognizeWithoutSettingsUpdateButNotRememberingSucceeds()
    {
        var order = NewExternal(ChargedShipping);
        var carrier = NewCarrier();
        var handler = NewHandler(
            order, new StubCarrierRepository(carrier), new RecordingQuotationAuditPublisher(),
            new StubExecutionContext(SubjectId, TenantId, TenancyPermissions.SettingsUpdate));

        await handler.HandleAsync(Command(order, carrier, remember: false), TestContext.Current.CancellationToken);

        Assert.Equal(carrier.Id.Value, order.CarrierId);
        Assert.Empty(carrier.ExternalNames);
    }

    [Fact]
    public async Task RecognizeOrderNotPendingThrows()
    {
        var order = NewExternal(ChargedShipping);
        order.Approve(Advisor, Now);
        var carrier = NewCarrier();

        await AssertCodeAsync("order.order.not_pending", NewHandler(order, carrier), Command(order, carrier));
    }

    [Fact]
    public async Task RecognizeOrderWithoutExternalTitleThrows()
    {
        var order = NewExternal(ChargedShipping, title: null);
        var carrier = NewCarrier();

        await AssertCodeAsync(
            "order.order.carrier_recognition_unavailable", NewHandler(order, carrier), Command(order, carrier));
    }

    [Fact]
    public async Task RecognizeInactiveCarrierThrows()
    {
        var order = NewExternal(ChargedShipping);
        var carrier = NewCarrier();
        carrier.Deactivate(Now);

        await AssertCodeAsync("order.order.carrier_not_available", NewHandler(order, carrier), Command(order, carrier));
    }

    [Fact]
    public async Task RecognizeUnknownCarrierThrows()
    {
        var order = NewExternal(ChargedShipping);
        var otherTenantCarrier = Carrier.Create(CarrierId.New(), Guid.CreateVersion7(), "Ajena", [], true, 1, Now);

        await AssertCodeAsync(
            "order.order.carrier_not_available",
            NewHandler(
                order, new StubCarrierRepository(otherTenantCarrier), new RecordingQuotationAuditPublisher(),
                new StubExecutionContext(SubjectId, TenantId)),
            Command(order, otherTenantCarrier));
    }

    [Fact]
    public async Task RecognizeCashOnDeliveryWithNonCollectingCarrierThrows()
    {
        var order = NewExternal(
            new ShippingDetails(null, null, FreightChargeMode.Free, 0m, 19, true), OrderPaymentStatus.PaymentPending);
        var carrier = NewCarrier(supportsCashOnDelivery: false);

        await AssertCodeAsync(
            "order.order.cash_on_delivery_not_supported", NewHandler(order, carrier), Command(order, carrier));
    }

    [Fact]
    public async Task RecognizeRememberAddsTitleAndKeepsFreight()
    {
        var order = NewExternal(ChargedShipping);
        var totalBefore = order.Total;
        var carrier = NewCarrier();
        var orders = new StubOrderListRepository(order);
        var audit = new RecordingQuotationAuditPublisher();
        var unitOfWork = new CountingSaveUnitOfWork();
        var handler = NewHandler(
            orders, new StubCarrierRepository(carrier), audit, new StubExecutionContext(SubjectId, TenantId),
            unitOfWork: unitOfWork);

        var detail = await handler.HandleAsync(Command(order, carrier), TestContext.Current.CancellationToken);

        Assert.Equal(
            new ShippingDetails(carrier.Id.Value, "Coordinadora", FreightChargeMode.Charged, 15_000m, 19, false),
            order.ShippingDetails);
        Assert.Equal(totalBefore, order.Total);
        Assert.Equal([Title], carrier.ExternalNames);
        Assert.Equal(["quotation.order.shipping_changed", "quotations.carrier.updated"], audit.Actions);
        var entry = Assert.Single(orders.History);
        Assert.Equal(OrderHistoryEventType.PartiesChanged, entry.EventType);
        Assert.Equal("Editó transportadora (Coordinadora).", entry.Details);
        Assert.Equal(1, unitOfWork.SaveCalls);
        Assert.Equal(carrier.Id.Value, detail.Order.Shipping.Carrier?.Id);
    }

    private static async Task AssertCodeAsync(
        string code, RecognizeOrderCarrierHandler handler, RecognizeOrderCarrierCommand command)
    {
        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            handler.HandleAsync(command, TestContext.Current.CancellationToken));
        Assert.Equal(code, error.Code);
    }

    private static RecognizeOrderCarrierCommand Command(Order order, Carrier carrier, bool remember = true) =>
        new(TenantId, order.Id.Value, carrier.Id.Value, remember);

    private static Carrier NewCarrier(bool supportsCashOnDelivery = true) =>
        Carrier.Create(CarrierId.New(), TenantId, "Coordinadora", [], supportsCashOnDelivery, 1, Now);

    private static Order NewExternal(
        ShippingDetails shipping,
        OrderPaymentStatus paymentStatus = OrderPaymentStatus.FullPaymentReceived,
        string? title = Title) =>
        Order.CreateExternal(
            OrderId.New(), TenantId, "PED-2026-0002",
            new OrderHeader(Guid.CreateVersion7(), Advisor, "COP", null, false, null, false, false),
            new QuotationParties(null, null, IsStorePickup: false), OrderBuilders.BillingAccount,
            [new OrderLineInput(Guid.CreateVersion7(), 10m, 11_900m, 0m, 19)], paymentStatus, null, Advisor, [], Now,
            OrderSource.Shopify, "#1001", shipping, "shop-1:1001", title);

    private static RecognizeOrderCarrierHandler NewHandler(Order order, Carrier carrier) =>
        NewHandler(
            order, new StubCarrierRepository(carrier), new RecordingQuotationAuditPublisher(),
            new StubExecutionContext(SubjectId, TenantId));

    private static RecognizeOrderCarrierHandler NewHandler(
        Order order,
        StubCarrierRepository carriers,
        RecordingQuotationAuditPublisher audit,
        IExecutionContext executionContext,
        ITenantModules? tenantModules = null) =>
        NewHandler(new StubOrderListRepository(order), carriers, audit, executionContext, tenantModules);

    private static RecognizeOrderCarrierHandler NewHandler(
        StubOrderListRepository orders,
        StubCarrierRepository carriers,
        RecordingQuotationAuditPublisher audit,
        IExecutionContext executionContext,
        ITenantModules? tenantModules = null,
        IQuotationsUnitOfWork? unitOfWork = null) =>
        new(
            orders,
            new StubQuotationListRepository(),
            carriers,
            tenantModules ?? FixedTenantModules.Simulated,
            unitOfWork ?? new NoOpQuotationsUnitOfWork(),
            audit,
            new StubMembershipDirectory(Advisor.Value),
            executionContext,
            new FixedClock(Now),
            new RecognizeOrderCarrierValidator());

    /// <summary>Cuenta los guardados: todo el reconocimiento —pedido, historial, transportadora y auditoría—
    /// va en una sola escritura.</summary>
    private sealed class CountingSaveUnitOfWork : IQuotationsUnitOfWork
    {
        public int SaveCalls { get; private set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
        {
            SaveCalls++;
            return Task.FromResult(0);
        }

        public Task<IQuotationsTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IQuotationsTransaction>(new NoOpQuotationsTransaction());

        public void DiscardPendingChanges()
        {
        }
    }
}
