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
    private static Order NewOrder(
        IReadOnlyCollection<OrderPaymentProofInput>? proofs = null,
        ShippingDetails? shipping = null,
        Guid? tenantId = null,
        string number = "PED-2026-0001") =>
        Order.CreateManual(
            OrderId.New(),
            tenantId ?? TenantId,
            number,
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
        Assert.Equal(["Facturado en Siigo, factura FV-1-123."], repository.History.Select(entry => entry.Details));
    }

    // Spec 2026-11-25 §4: la factura se anuló en Siigo y QEP lo verificó: el pedido pierde la marca de esa factura.
    [Fact]
    public void RevertingTheSameExternalMarkClearsItAndBumpsTheVersion()
    {
        var order = NewOrder();
        order.InvoiceExternally("siigo", "FV-1-1", "id-1", Biller, Now);
        var version = order.Version;

        var released = order.RevertExternalInvoicing("siigo", "FV-1-1", Advisor, Now.AddMinutes(5));

        Assert.True(released);
        Assert.False(order.IsInvoiced);
        Assert.Null(order.InvoicedAt);
        Assert.Null(order.InvoicedBy);
        Assert.Null(order.InvoiceProvider);
        Assert.Null(order.InvoiceNumber);
        Assert.Null(order.InvoiceExternalId);
        Assert.Equal(Now.AddMinutes(5), order.UpdatedAt);
        Assert.Equal(version + 1, order.Version);
    }

    // Sin marca, o con otra, no hay nada que soltar: una marca manual es una declaración de la persona que esta acción no borra.
    [Theory]
    [InlineData("none")]
    [InlineData("manual")]
    [InlineData("other_number")]
    [InlineData("other_provider")]
    public void RevertingAnotherMarkLeavesTheOrderAsItWas(string mark)
    {
        var order = NewOrder();
        switch (mark)
        {
            case "manual":
                order.Invoice(Advisor, requireFullPayment: false, Now);
                break;
            case "other_number":
                order.InvoiceExternally("siigo", "FV-1-2", "id-2", Biller, Now);
                break;
            case "other_provider":
                order.InvoiceExternally("otro", "FV-1-1", "id-1", Biller, Now);
                break;
        }

        var before = (order.Version, order.UpdatedAt, order.InvoicedAt, order.InvoicedBy, order.InvoiceProvider, order.InvoiceNumber, order.InvoiceExternalId);

        var released = order.RevertExternalInvoicing("siigo", "FV-1-1", Advisor, Now.AddMinutes(5));

        Assert.False(released);
        Assert.Equal(
            before,
            (order.Version, order.UpdatedAt, order.InvoicedAt, order.InvoicedBy, order.InvoiceProvider, order.InvoiceNumber, order.InvoiceExternalId));
    }

    [Fact]
    public async Task RevertExternalInvoicingWritesTheHistoryAndTheAuditOnlyWhenItReleases()
    {
        var order = NewOrder();
        order.InvoiceExternally("siigo", "FV-1-1", "id-1", Biller, Now);
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

        var first = await service.RevertExternalInvoicingAsync(
            TenantId, order.Id.Value, "siigo", "FV-1-1", Biller.Value, TestContext.Current.CancellationToken);
        var second = await service.RevertExternalInvoicingAsync(
            TenantId, order.Id.Value, "siigo", "FV-1-1", Biller.Value, TestContext.Current.CancellationToken);

        Assert.Equal((true, false), (first, second));
        Assert.False(order.IsInvoiced);
        var entry = Assert.Single(repository.History);
        Assert.Equal(
            (OrderHistoryEventType.InvoicingReverted, "Se anuló en Siigo la factura FV-1-1; el pedido quedó sin facturar."),
            (entry.EventType, entry.Details));
        Assert.Equal(["quotation.order.invoice_reverted"], audit.Actions);
        var details = Assert.Single(audit.Details);
        Assert.Equal(("siigo", "FV-1-1"), (details["provider"], details["number"]));
    }

    // Spec 2026-11-24 §5: las tandas de facturación pintan el número y el cliente que muestra /orders, en una consulta de cada uno; un
    // pedido de otro tenant o que no existe no aparece.
    [Fact]
    public async Task ListLabelsReturnsTheNumberAndCustomerNameOfTheTenantOrdersOnly()
    {
        var own = NewOrder();
        var foreign = NewOrder(tenantId: Guid.CreateVersion7(), number: "PED-2026-0009");
        var customers = new StubQuotationCustomerLookup(new QuotationCustomerRef(
            ClientId, TenantId, "CUC-001", IsActive: true, "Ferretería El Tornillo", "3001234567", "Calle 1 # 2-3",
            WithRetention: false, VatSurplus: false));
        var service = new ExternalOrderInvoicingService(
            new StubOrderListRepository(own, foreign),
            new NoOpQuotationsUnitOfWork(),
            new RecordingQuotationAuditPublisher(),
            new SubjectExecutionContext(Guid.CreateVersion7(), TenantId),
            new FixedClock(Now),
            new FixedTenantClock(Now),
            new FixedQuotationSettingsStore(),
            new StubTenantDirectory(),
            customers,
            new StubQuotationAdvisorLookup(),
            new StubQuotationGeographyLookup(new Dictionary<Guid, string>()));

        var labels = await service.ListLabelsAsync(
            TenantId, [own.Id.Value, foreign.Id.Value, Guid.CreateVersion7()], TestContext.Current.CancellationToken);

        var label = Assert.Single(labels);
        Assert.Equal(own.Id.Value, label.Key);
        Assert.Equal(new ExternalInvoicingOrderLabel("PED-2026-0001", "Ferretería El Tornillo"), label.Value);
        Assert.Equal(1, customers.FindNamesCalls);
    }

    // La recuperación de una tanda sólo pregunta: marcado es la marca de ese proveedor con ese número, no cualquier «facturado».
    [Fact]
    public async Task IsMarkedIsTrueOnlyForTheSameProviderAndNumber()
    {
        var marked = NewOrder();
        marked.InvoiceExternally("siigo", "FV-1-123", "a1b2c3", Biller, Now);
        var manual = NewOrder(number: "PED-2026-0002");
        manual.Invoice(Advisor, requireFullPayment: false, Now);
        var unmarked = NewOrder(number: "PED-2026-0003");
        var service = new ExternalOrderInvoicingService(
            new StubOrderListRepository(marked, manual, unmarked),
            new NoOpQuotationsUnitOfWork(),
            new RecordingQuotationAuditPublisher(),
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

        Task<bool> IsMarked(Order order, string provider, string number) =>
            service.IsMarkedAsync(TenantId, order.Id.Value, provider, number, TestContext.Current.CancellationToken);

        Assert.True(await IsMarked(marked, "siigo", "FV-1-123"));
        Assert.False(await IsMarked(marked, "siigo", "FV-1-124"));
        Assert.False(await IsMarked(marked, "otro", "FV-1-123"));
        Assert.False(await IsMarked(manual, "siigo", "FV-1-123"));
        Assert.False(await IsMarked(unmarked, "siigo", "FV-1-123"));
        Assert.False(await IsMarked(NewOrder(number: "PED-2026-0004"), "siigo", "FV-1-123"));
        Assert.False(unmarked.IsInvoiced);
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

    // 10 u. pagas a 1.000 (IVA 19 incluido) y un regalo de 1 u. de la misma variante: la factura externa tiene que ver el regalo marcado,
    // con su precio de lista, y sin sumar al total de productos.
    private static (Order Order, Guid VariantId) OrderWithAGiftOfThePaidVariant()
    {
        var variantId = Guid.CreateVersion7();
        var productId = Guid.CreateVersion7();
        var order = Order.CreateManual(
            OrderId.New(),
            TenantId,
            "PED-2026-0002",
            new OrderHeader(ClientId, Advisor, "COP", "Transferencia", false, null, false, []),
            QuotationParties.Empty,
            Account,
            ShippingDetails.None,
            [new OrderLineInput(variantId, productId, 10m, 1_000m, 0m, 19, new QuotationProductSnapshot("ABC", "Aceite"))],
            null,
            Advisor,
            [new OrderPaymentProofInput(Guid.CreateVersion7(), 10_000m)],
            Now,
            orderDate: DateOnly.FromDateTime(Now.UtcDateTime));
        order.AddGift(OrderItemId.New(), variantId, productId, 1m, 1_000m, 19, OrderLineCosts.None, Advisor, Now);
        return (order, variantId);
    }

    [Fact]
    public async Task GetForInvoicingMapsAGiftLineWithItsFlagAndListPrice()
    {
        var (order, variantId) = OrderWithAGiftOfThePaidVariant();
        var service = NewService(
            order,
            new QuotationCustomerRef(
                ClientId, TenantId, "CUC-001", IsActive: true, "Ferretería El Tornillo", "3001234567", "Calle 1 # 2-3",
                WithRetention: false, VatSurplus: false, IdentificationNumber: "900123456", IdentificationType: "Nit"),
            new Dictionary<Guid, string>());

        var invoicing = await service.GetForInvoicingAsync(TenantId, order.Id.Value, TestContext.Current.CancellationToken);

        Assert.NotNull(invoicing);
        Assert.Equal(2, invoicing.Lines.Count);
        var paid = Assert.Single(invoicing.Lines, line => !line.IsGift);
        var gift = Assert.Single(invoicing.Lines, line => line.IsGift);
        Assert.Equal(variantId, gift.VariantId);
        Assert.Equal(1_000m, gift.ListUnitPrice);
        Assert.Equal(0m, gift.Subtotal);
        Assert.Equal(0m, gift.TaxAmount);
        Assert.Equal(10m, paid.Quantity);
        // El regalo no suma a los productos: sólo la línea pagada (10.000 con IVA).
        Assert.Equal(paid.Subtotal + paid.TaxAmount, invoicing.ProductsTotal);
        Assert.Equal(10_000m, invoicing.ProductsTotal);
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
