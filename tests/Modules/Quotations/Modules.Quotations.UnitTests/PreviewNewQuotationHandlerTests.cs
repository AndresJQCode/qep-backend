using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El cálculo de una cotización nueva sin crearla (spec 2026-10-18 §7.3): mismo armado que la creación, sin
/// número, sin escritura y sin exigir el documento de la facturación mientras la persona escribe.
/// </summary>
public sealed class PreviewNewQuotationHandlerTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly Guid SubjectId = Guid.CreateVersion7();
    private static readonly Guid MembershipId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task PreviewReturnsTheDocumentWithoutIdNumberOrVersion()
    {
        var productId = Guid.CreateVersion7();
        var handler = Handler(pricing: new StubQuotationProductPricingLookup(Product(productId, "Vela de soja", 100_000m)));

        var preview = await handler.HandleAsync(Query(new QuotationItemAddition(productId, 3m)), CancellationToken.None);

        Assert.Equal(300_000m, preview.Total);
        Assert.Equal(Guid.Empty, preview.Id);
        Assert.Equal(string.Empty, preview.QuotationNumber);
        Assert.Equal(0, preview.Version);
    }

    // El candado contra el que mañana «aprovecha» y guarda algo en el cálculo previo.
    [Fact]
    public void PreviewDoesNotSpendANumberNorWriteAnything()
    {
        Assert.DoesNotContain(
            typeof(PreviewNewQuotationHandler).GetConstructors().Single().GetParameters(),
            parameter => parameter.ParameterType == typeof(IQuotationNumberGenerator)
                || parameter.ParameterType == typeof(IQuotationsUnitOfWork)
                || parameter.ParameterType == typeof(IQuotationRepository)
                || parameter.ParameterType == typeof(IQuotationAuditPublisher));
    }

    [Fact]
    public async Task PreviewWithoutValidityShowsTheDefaultOne()
    {
        var preview = await Handler().HandleAsync(Query(), CancellationToken.None);

        // Hoy de Bogotá (2026-10-08) + 15 días: la fecha que dejaría crear.
        Assert.Equal(new DateOnly(2026, 10, 23), preview.ValidUntil);
    }

    [Fact]
    public async Task PreviewDoesNotRequireTheBillingIdentification()
    {
        var parties = new QuotationPartiesRequest(
            new QuotationPartyRequest(
                "Distribuciones Andinas S.A.S.", IdentificationNumber: null, null, null, null, null, null),
            null);

        var preview = await Handler().HandleAsync(Query() with { Parties = parties }, CancellationToken.None);

        Assert.Contains(preview.Parties, party => party.Name == "Distribuciones Andinas S.A.S.");
    }

    [Fact]
    public async Task PreviewAppliesTheCustomerEligibility()
    {
        var handler = Handler(customer: Customer(isActive: false));

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(
            () => handler.HandleAsync(Query(), CancellationToken.None));

        Assert.Equal("quotation.quotation.client_inactive", error.Code);
    }

    [Fact]
    public async Task PreviewRejectsRetailWithAFloor()
    {
        var productId = Guid.CreateVersion7();
        var handler = Handler(pricing: new StubQuotationProductPricingLookup(Product(productId, "Vela de soja", 100_000m)));

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() => handler.HandleAsync(
            Query(new QuotationItemAddition(productId, 2m)) with { IsRetail = true, GlobalScaleFloor = 2 },
            CancellationToken.None));

        Assert.Equal("quotation.retail.floor_not_allowed", error.Code);
    }

    // Una cotización nueva no tiene modo guardado: un modo apagado se rechaza como al crear (spec 2026-10-17 §4.4).
    [Fact]
    public async Task PreviewRejectsADisabledFreightChargeMode()
    {
        var settings = new FixedQuotationSettingsStore
        {
            EnabledFreightChargeModes = new HashSet<FreightChargeMode> { FreightChargeMode.Charged },
        };

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() => Handler(settings: settings).HandleAsync(
            Query() with { Shipping = new ShippingRequest(null, "Free", 0m, false) }, CancellationToken.None));

        Assert.Equal("quotation.quotation.freight_charge_mode_disabled", error.Code);
    }

    [Fact]
    public async Task PreviewAndCreateGiveTheSameTotal()
    {
        var soy = Guid.CreateVersion7();
        var beeswax = Guid.CreateVersion7();
        var scales = new[] { new QuotationPriceScaleRef(2, 100, 10m, QuotationPriceScaleRestriction.Multiple, 1, []) };
        var pricing = new StubQuotationProductPricingLookup(
            Product(soy, "Vela de soja", 100_000m) with { Scales = scales, TaxPercentage = 19 },
            Product(beeswax, "Vela de cera de abejas", 80_000m) with { Scales = scales });
        // Diez unidades y más de 500.000: pasan el mínimo de compra por defecto, así que el descuento queda.
        QuotationItemAddition[] items = [new(soy, 5m), new(beeswax, 5m)];

        var preview = await Handler(pricing: pricing).HandleAsync(
            Query(items) with { GlobalScaleFloor = 2 }, CancellationToken.None);
        var created = await CreateHandler(pricing).HandleAsync(
            new CreateQuotationCommand(
                TenantId, ClientId, null, null, null, null, null, Items: items, GlobalScaleFloor: 2),
            CancellationToken.None);

        Assert.Equal(created.Total, preview.Total);
        Assert.Equal(created.Subtotal, preview.Subtotal);
        Assert.Equal(created.TaxAmount, preview.TaxAmount);
        Assert.Equal(created.DiscountAmount, preview.DiscountAmount);
        Assert.True(preview.DiscountAmount > 0m);
    }

    private static QuotationProductPricingRef Product(Guid id, string name, decimal price) =>
        new(id, TenantId, name, true, new Dictionary<string, decimal> { ["COP"] = price }, [], null);

    private static QuotationCustomerRef Customer(bool isActive = true) =>
        new(ClientId, TenantId, "CUC-001", isActive, "Ferretería El Tornillo",
            "3001234567", "Calle 1 # 2-3", WithRetention: false, VatSurplus: false);

    private static PreviewNewQuotationQuery Query(params QuotationItemAddition[] items) =>
        new(TenantId, ClientId, ValidUntil: null, PaymentMethod: null, Notes: null, Parties: null,
            BillingAccount: null, items, GlobalScaleFloor: null, IsRetail: false);

    private static QuotationDraftBuilder Drafts(
        StubQuotationProductPricingLookup? pricing,
        QuotationCustomerRef? customer,
        FixedQuotationSettingsStore? settings)
    {
        settings ??= new FixedQuotationSettingsStore();
        return new QuotationDraftBuilder(
            new StubQuotationCustomerLookup(customer ?? Customer()),
            new StubQuotationCompanyLookup(new Dictionary<Guid, QuotationCompanyRef>()),
            pricing ?? new StubQuotationProductPricingLookup(),
            settings,
            new ShippingResolver(new StubCarrierRepository(), new StubQuotationTaxRateLookup(null), settings),
            new FixedTenantDefaultCurrency());
    }

    private static PreviewNewQuotationHandler Handler(
        StubQuotationProductPricingLookup? pricing = null,
        QuotationCustomerRef? customer = null,
        FixedQuotationSettingsStore? settings = null) =>
        new(
            Drafts(pricing, customer, settings),
            new StubMembershipDirectory(MembershipId),
            new StubExecutionContext(SubjectId, TenantId),
            new FixedTenantClock(Now),
            new PreviewNewQuotationValidator());

    private static CreateQuotationHandler CreateHandler(StubQuotationProductPricingLookup pricing) =>
        new(
            new StubQuotationRepository(Quotation.Create(
                QuotationId.New(), TenantId, "QUO-2026-0001", ClientId, new MemberId(MembershipId),
                null, null, null, QuotationParties.Empty, null, "COP", false, false,
                new MemberId(MembershipId), Now)),
            new NoOpQuotationsUnitOfWork(),
            new NoOpQuotationAuditPublisher(),
            new StubQuotationProductLookup(new Dictionary<Guid, QuotationProductRef>()),
            Drafts(pricing, null, null),
            new SequentialQuotationNumberGenerator(),
            new DefaultNumberingFormats(),
            new StubMembershipDirectory(MembershipId),
            new StubExecutionContext(SubjectId, TenantId),
            new FixedTenantClock(Now),
            new CreateQuotationValidator());
}
