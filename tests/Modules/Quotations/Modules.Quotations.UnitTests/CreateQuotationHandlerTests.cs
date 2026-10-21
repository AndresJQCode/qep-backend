using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

public sealed class CreateQuotationHandlerTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly Guid SubjectId = Guid.CreateVersion7();
    private static readonly Guid MembershipId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CreateReadsTheTenantDefaultCurrencyForTheActiveTenant()
    {
        var currency = new FixedTenantDefaultCurrency("USD");
        var handler = Handler(defaultCurrency: currency);

        var created = await handler.HandleAsync(CreateCommand(billingAccount: null), CancellationToken.None);

        Assert.Equal("USD", created.Currency);
        Assert.Equal([TenantId], currency.RequestedTenantIds);
    }

    [Fact]
    public async Task CreateWithItemsPricesTheLinesInTheBackend()
    {
        var productId = Guid.CreateVersion7();
        var handler = Handler(pricing: new StubQuotationProductPricingLookup(Product(productId, "Vela de soja", 100_000m)));

        var created = await handler.HandleAsync(
            CreateCommand(billingAccount: null) with { Items = [new(productId, 2m)] }, CancellationToken.None);

        var line = Assert.Single(created.Items);
        Assert.Equal(100_000m, line.UnitPrice);
        Assert.Equal(2m, line.Quantity);
        Assert.Equal(200_000m, created.Total);
        Assert.Equal("Draft", created.Status);
    }

    [Fact]
    public async Task CreateKeepsTheGlobalScaleFloor()
    {
        var productId = Guid.CreateVersion7();
        var handler = Handler(pricing: new StubQuotationProductPricingLookup(Product(productId, "Vela de soja", 100_000m)));

        var created = await handler.HandleAsync(
            CreateCommand(billingAccount: null) with { Items = [new(productId, 2m)], GlobalScaleFloor = 2 },
            CancellationToken.None);

        Assert.Equal(2, created.GlobalScaleFloor);
    }

    [Fact]
    public async Task CreateKeepsRetail()
    {
        var productId = Guid.CreateVersion7();
        // Con escala desde 10 unidades: sin el detal, 12 unidades llevarían descuento.
        var product = Product(productId, "Vela de soja", 100_000m) with
        {
            Scales = [new QuotationPriceScaleRef(10, 100, 10m, QuotationPriceScaleRestriction.Multiple, 1, [])],
        };
        var handler = Handler(pricing: new StubQuotationProductPricingLookup(product));

        var created = await handler.HandleAsync(
            CreateCommand(billingAccount: null) with { Items = [new(productId, 12m)], IsRetail = true },
            CancellationToken.None);

        Assert.True(created.IsRetail);
        Assert.All(created.Items, item => Assert.Equal(0m, item.DiscountPercentage));
    }

    [Fact]
    public async Task CreateRejectsRetailWithAFloor()
    {
        var productId = Guid.CreateVersion7();
        var handler = Handler(pricing: new StubQuotationProductPricingLookup(Product(productId, "Vela de soja", 100_000m)));

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() => handler.HandleAsync(
            CreateCommand(billingAccount: null) with
            {
                Items = [new(productId, 2m)],
                IsRetail = true,
                GlobalScaleFloor = 2,
            },
            CancellationToken.None));

        Assert.Equal("quotation.retail.floor_not_allowed", error.Code);
    }

    [Fact]
    public async Task CreateWithoutItemsIsTheEmptyDraftOfBefore()
    {
        var created = await Handler().HandleAsync(CreateCommand(billingAccount: null), CancellationToken.None);

        Assert.Empty(created.Items);
        Assert.Equal(0m, created.Total);
        // Hoy de Bogotá (2026-10-08) + 15 días.
        Assert.Equal(new DateOnly(2026, 10, 23), created.ValidUntil);
    }

    // Ruling L1: el contrato de antes deja exactamente lo de antes, versión incluida.
    [Fact]
    public async Task CreateWithOnlyTheClientStartsAtVersionOneWithOnlyCreated()
    {
        var repository = EmptyRepository();
        var audit = new RecordingQuotationAuditPublisher();

        var created = await Handler(repository: repository, audit: audit).HandleAsync(
            CreateCommand(billingAccount: null), CancellationToken.None);

        Assert.Equal(1, created.Version);
        var entry = Assert.Single(repository.HistoryEntries);
        Assert.Equal(QuotationHistoryEventType.Created, entry.EventType);
        Assert.Equal(["quotation.quotation.created"], audit.Actions);
    }

    [Fact]
    public async Task CreateWithItemsRecordsCreatedAndOneItemAddedPerLine()
    {
        var soy = Guid.CreateVersion7();
        var beeswax = Guid.CreateVersion7();
        var repository = EmptyRepository();
        var audit = new RecordingQuotationAuditPublisher();
        var handler = Handler(
            pricing: new StubQuotationProductPricingLookup(
                Product(soy, "Vela de soja", 100_000m), Product(beeswax, "Vela de cera de abejas", 80_000m)),
            repository: repository,
            audit: audit);

        await handler.HandleAsync(
            CreateCommand(billingAccount: null) with
            {
                Notes = "Entregar el lunes",
                Shipping = new ShippingRequest(null, "Free", 0m, false),
                Items = [new(soy, 2m), new(beeswax, 1m)],
            },
            CancellationToken.None);

        // Sin fila Edited del encabezado aunque el cuerpo traiga notas y envío (spec §7.4).
        Assert.Equal(
            [QuotationHistoryEventType.Created, QuotationHistoryEventType.Edited, QuotationHistoryEventType.Edited],
            repository.HistoryEntries.Select(entry => entry.EventType));
        Assert.Equal(
            ["quotation.quotation.created", "quotation.quotation.item_added", "quotation.quotation.item_added"],
            audit.Actions);
    }

    [Fact]
    public async Task CreateWithoutItemsRecordsOnlyCreated()
    {
        var repository = EmptyRepository();
        var audit = new RecordingQuotationAuditPublisher();

        await Handler(repository: repository, audit: audit).HandleAsync(
            CreateCommand(billingAccount: null) with { Notes = "Entregar el lunes" }, CancellationToken.None);

        Assert.Equal(QuotationHistoryEventType.Created, Assert.Single(repository.HistoryEntries).EventType);
        Assert.Equal(["quotation.quotation.created"], audit.Actions);
    }

    // Spec 2026-10-17 §4.4: una cotización nueva no tiene modo guardado, así que cualquier modo del cuerpo es
    // "elegido ahora" y uno apagado se rechaza, con líneas o sin ellas.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateRejectsADisabledFreightChargeMode(bool withItems)
    {
        var productId = Guid.CreateVersion7();
        var settings = new FixedQuotationSettingsStore
        {
            EnabledFreightChargeModes = new HashSet<FreightChargeMode> { FreightChargeMode.Charged },
        };
        var numbers = new SequentialQuotationNumberGenerator();
        var handler = Handler(
            pricing: new StubQuotationProductPricingLookup(Product(productId, "Vela de soja", 100_000m)),
            settings: settings,
            numbers: numbers);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() => handler.HandleAsync(
            CreateCommand(billingAccount: null) with
            {
                Shipping = new ShippingRequest(null, "Free", 0m, false),
                Items = withItems ? [new(productId, 2m)] : null,
            },
            CancellationToken.None));

        Assert.Equal("quotation.quotation.freight_charge_mode_disabled", error.Code);
    }

    internal static QuotationVariantPricingRef Product(Guid id, string name, decimal price) =>
        new(id, id, TenantId, name, "SKU", true, new Dictionary<string, decimal> { ["COP"] = price }, [], null);

    private static CreateQuotationCommand CreateCommand(QuotationBillingAccountRequest? billingAccount) =>
        new(TenantId, ClientId, ValidUntil: null, PaymentMethod: null, Notes: null, Parties: null, billingAccount);

    private static StubQuotationRepository EmptyRepository() =>
        new(Quotation.Create(
            QuotationId.New(), TenantId, "QUO-2026-0001", ClientId, new MemberId(MembershipId),
            null, null, null, QuotationParties.Empty, null, "COP", false, false,
            new MemberId(MembershipId), Now));

    private static CreateQuotationHandler Handler(
        FixedTenantDefaultCurrency? defaultCurrency = null,
        StubQuotationProductPricingLookup? pricing = null,
        StubQuotationRepository? repository = null,
        IQuotationAuditPublisher? audit = null,
        SequentialQuotationNumberGenerator? numbers = null,
        FixedQuotationSettingsStore? settings = null)
    {
        settings ??= new FixedQuotationSettingsStore();
        var drafts = new QuotationDraftBuilder(
            new StubQuotationCustomerLookup(new QuotationCustomerRef(
                ClientId, TenantId, "CUC-001", IsActive: true, "Ferretería El Tornillo",
                "3001234567", "Calle 1 # 2-3", WithRetention: false, VatSurplus: false)),
            new StubQuotationCompanyLookup(new Dictionary<Guid, QuotationCompanyRef>()),
            pricing ?? new StubQuotationProductPricingLookup(),
            settings,
            new ShippingResolver(new StubCarrierRepository(), new StubQuotationTaxRateLookup(null), settings),
            defaultCurrency ?? new FixedTenantDefaultCurrency());

        return new(
            repository ?? EmptyRepository(),
            new NoOpQuotationsUnitOfWork(),
            audit ?? new NoOpQuotationAuditPublisher(),
            new StubQuotationProductLookup(new Dictionary<Guid, QuotationVariantRef>()),
            drafts,
            numbers ?? new SequentialQuotationNumberGenerator(),
            new DefaultNumberingFormats(),
            new StubMembershipDirectory(MembershipId),
            new StubExecutionContext(SubjectId, TenantId),
            new FixedTenantClock(Now),
            new CreateQuotationValidator());
    }
}
