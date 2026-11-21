using BuildingBlocks.Application;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>Spec 2026-11-18 §5.5: un pedido facturado por un proveedor externo (Siigo) lleva su marca, no exige pagado, es idempotente y
/// no se revierte desde QEP.</summary>
public sealed class OrderExternalInvoicingTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly MemberId Advisor = new(Guid.CreateVersion7());
    private static readonly MemberId Biller = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    private static QuotationBillingAccount Account => new()
    {
        CompanyId = Guid.CreateVersion7(),
        BankName = "Bancolombia",
        AccountNumber = "7542",
        Currency = "COP",
    };

    // Una línea de 2 × 11.900 con IVA del 19 % → neto 23.800. Por defecto, un comprobante de 100.000 (queda pagado).
    private static Order NewOrder(IReadOnlyCollection<OrderPaymentProofInput>? proofs = null, ShippingDetails? shipping = null) =>
        Order.CreateManual(
            OrderId.New(),
            TenantId,
            "PED-2026-0001",
            new OrderHeader(ClientId, Advisor, "COP", "Transferencia", false, null, false, []),
            QuotationParties.Empty,
            Account,
            shipping ?? ShippingDetails.None,
            [new OrderLineInput(Guid.CreateVersion7(), Guid.CreateVersion7(), 2m, 11_900m, 0m, 19, new QuotationProductSnapshot("ABC", "Aceite"))],
            null,
            Advisor,
            proofs ?? [new OrderPaymentProofInput(Guid.CreateVersion7(), 100_000m)],
            Now,
            orderDate: DateOnly.FromDateTime(Now.UtcDateTime));

    private static string CodeOf(Action action) => Assert.Throws<QuotationsDomainException>(action).Code;

    [Fact]
    public void InvoiceExternallyDoesNotRequirePayment()
    {
        var order = NewOrder(proofs: []);
        Assert.NotEqual(OrderPaymentStatus.FullPaymentReceived, order.PaymentStatus);
        var version = order.Version;

        order.InvoiceExternally("siigo", "FV-1-123", "a1b2c3", Biller, Now);

        Assert.True(order.IsInvoiced);
        Assert.Equal(("siigo", "FV-1-123", "a1b2c3"), (order.InvoiceProvider, order.InvoiceNumber, order.InvoiceExternalId));
        Assert.Equal(Biller, order.InvoicedBy);
        Assert.Equal(Now, order.InvoicedAt);
        Assert.Equal(version + 1, order.Version);
        Assert.False(order.IsEditable);
    }

    [Fact]
    public void InvoiceExternallyIsIdempotentForTheSameNumber()
    {
        var order = NewOrder();
        order.InvoiceExternally("siigo", "FV-1-123", "a1b2c3", Biller, Now);
        var version = order.Version;

        order.InvoiceExternally("siigo", "FV-1-123", "a1b2c3", Advisor, Now.AddMinutes(5));

        Assert.Equal(version, order.Version);
        Assert.Equal(Biller, order.InvoicedBy);
        Assert.Equal(Now, order.InvoicedAt);
    }

    [Fact]
    public void InvoiceExternallyOverAManualMarkFails()
    {
        var order = NewOrder();
        order.Invoice(Advisor, requireFullPayment: false, Now);

        Assert.Equal("order.order.already_invoiced", CodeOf(() => order.InvoiceExternally("siigo", "FV-1-123", "a1b2c3", Biller, Now)));
        Assert.Null(order.InvoiceProvider);
    }

    [Fact]
    public void InvoiceExternallyWithAnotherNumberFails()
    {
        var order = NewOrder();
        order.InvoiceExternally("siigo", "FV-1-123", "a1b2c3", Biller, Now);

        Assert.Equal("order.order.already_invoiced", CodeOf(() => order.InvoiceExternally("siigo", "FV-1-124", "d4e5f6", Biller, Now)));
        Assert.Equal("FV-1-123", order.InvoiceNumber);
    }

    [Fact]
    public void ACancelledOrderCannotBeInvoicedExternally()
    {
        var order = NewOrder();
        order.Cancel(Advisor, "Duplicado", Now);

        Assert.Equal("order.order.already_cancelled", CodeOf(() => order.InvoiceExternally("siigo", "FV-1-123", "a1b2c3", Biller, Now)));
    }

    [Fact]
    public void RevertingAnExternallyInvoicedOrderIs422()
    {
        var order = NewOrder();
        order.InvoiceExternally("siigo", "FV-1-123", "a1b2c3", Biller, Now);

        Assert.Equal("order.order.invoiced_externally", CodeOf(() => order.RevertInvoicing(Advisor, Now)));
        Assert.True(order.IsInvoiced);
        Assert.Equal("FV-1-123", order.InvoiceNumber);
    }

    [Fact]
    public void RevertingAManualMarkStillWorksAndLeavesNoProvider()
    {
        var order = NewOrder();
        order.Invoice(Advisor, requireFullPayment: false, Now);

        order.RevertInvoicing(Advisor, Now);

        Assert.False(order.IsInvoiced);
        Assert.Null(order.InvoiceProvider);
    }

    [Fact]
    public async Task MarkInvoicedWritesTheHistoryAndTheAuditWithTheProviderAndIsIdempotent()
    {
        var order = NewOrder();
        var repository = new StubOrderListRepository(order);
        var audit = new RecordingQuotationAuditPublisher();
        var service = new ExternalOrderInvoicingService(
            repository,
            new NoOpQuotationsUnitOfWork(),
            audit,
            new SubjectExecutionContext(Guid.CreateVersion7(), TenantId),
            new FixedClock(Now),
            new FixedTenantClock(Now),
            new FixedQuotationSettingsStore(),
            new StubTenantDirectory(),
            new StubQuotationCustomerLookup(new QuotationCustomerRef(
                ClientId, TenantId, "CUC-001", IsActive: true, "Ferretería El Tornillo", "3001234567", "Calle 1 # 2-3",
                WithRetention: false, VatSurplus: false)),
            new StubQuotationAdvisorLookup(),
            new StubQuotationGeographyLookup(new Dictionary<Guid, string>()));

        await service.MarkInvoicedAsync(TenantId, order.Id.Value, "siigo", "FV-1-123", "a1b2c3", Biller.Value, TestContext.Current.CancellationToken);
        await service.MarkInvoicedAsync(TenantId, order.Id.Value, "siigo", "FV-1-123", "a1b2c3", Biller.Value, TestContext.Current.CancellationToken);

        Assert.Equal("FV-1-123", order.InvoiceNumber);
        Assert.Equal(["quotation.order.invoiced"], audit.Actions);
        Assert.Equal([OrderHistoryEventType.Invoiced], repository.History.Select(entry => entry.EventType));
    }

    // «Los mismos del cliente» no guarda parte de envío: el despacho va a la dirección y ciudad de la ficha del cliente, como la facturación.
    [Fact]
    public async Task GetForInvoicingShipsToTheCustomersAddressAndCityWhenTheOrderHasNoShippingParty()
    {
        // Envío gratis y sin transportadora: hay envío registrado, y ninguna parte de envío propia.
        var order = NewOrder(shipping: new ShippingDetails(null, null, FreightChargeMode.Free, 0m, 0, CashOnDelivery: false));
        Assert.Null(order.Shipping);
        var cityId = Guid.CreateVersion7();
        var service = NewService(
            order,
            new QuotationCustomerRef(
                ClientId, TenantId, "CUC-001", IsActive: true, "Ferretería El Tornillo", "3001234567", "Cra 9 # 13-135 bodega 501",
                WithRetention: false, VatSurplus: false, CityId: cityId, DepartmentId: Guid.CreateVersion7(),
                IdentificationNumber: "900123456", IdentificationType: "Nit"),
            new Dictionary<Guid, string> { [cityId] = "Cali" });

        var invoicing = await service.GetForInvoicingAsync(TenantId, order.Id.Value, TestContext.Current.CancellationToken);

        Assert.NotNull(invoicing);
        Assert.Equal("Cra 9 # 13-135 bodega 501", invoicing.ShippingAddress);
        Assert.Equal("Cali", invoicing.ShippingCity);
    }

    // Sin envío registrado no hay despacho: la dirección del cliente no se vuelve «dirección de despacho».
    [Fact]
    public async Task GetForInvoicingHasNoShippingAddressWhenTheOrderHasNoShippingRegistered()
    {
        var order = NewOrder();
        var service = NewService(
            order,
            new QuotationCustomerRef(
                ClientId, TenantId, "CUC-001", IsActive: true, "Ferretería El Tornillo", "3001234567", "Cra 9 # 13-135 bodega 501",
                WithRetention: false, VatSurplus: false, IdentificationNumber: "900123456", IdentificationType: "Nit"),
            new Dictionary<Guid, string>());

        var invoicing = await service.GetForInvoicingAsync(TenantId, order.Id.Value, TestContext.Current.CancellationToken);

        Assert.NotNull(invoicing);
        Assert.Null(invoicing.ShippingAddress);
        Assert.Null(invoicing.ShippingCity);
    }

    private static ExternalOrderInvoicingService NewService(
        Order order, QuotationCustomerRef customer, IReadOnlyDictionary<Guid, string> cityNames) =>
        new(
            new StubOrderListRepository(order),
            new NoOpQuotationsUnitOfWork(),
            new RecordingQuotationAuditPublisher(),
            new SubjectExecutionContext(Guid.CreateVersion7(), TenantId),
            new FixedClock(Now),
            new FixedTenantClock(Now),
            new FixedQuotationSettingsStore { OrderSettings = new OrderSettings(false, false, false) },
            new StubTenantDirectory(),
            new StubQuotationCustomerLookup(customer),
            new StubQuotationAdvisorLookup(),
            new StubQuotationGeographyLookup(cityNames));

    private sealed class SubjectExecutionContext(Guid subjectId, Guid tenantId) : IExecutionContext
    {
        public Guid SubjectId { get; } = subjectId;

        public TenantId TenantId { get; } = new(tenantId);

        public bool HasPermission(string permission) => true;
    }
}
