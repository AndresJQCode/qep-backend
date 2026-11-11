using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Quotations.UnitTests;

// Politica de campos del cliente: aprobar exige documento y ciudad de envio del cliente facturado segun la politica
// del tenant. Estas pruebas fijan la regla; la del handler vive en MarkOrderPaidHandlerTests.
public sealed class BilledCustomerFieldsRequiredTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 15, 0, 0, TimeSpan.Zero);

    // P3: el constructor publico, no Validated (interno).
    private static readonly ShippingDetails Freight =
        new(Guid.CreateVersion7(), "Servientrega", FreightChargeMode.Charged, 15_000m, 0, false);

    private static readonly FieldRequirement NotOnBilling = new(true, false);
    private static readonly FieldRequirement OnBilling = new(false, true);

    private static StubTenantDirectory Directory(FieldRequirement document, FieldRequirement city) =>
        new()
        {
            Regional = new(
                "America/Bogota", "dd/MM/yyyy", "COP", TenantNumberFormats.CommaDecimal,
                CustomerFieldPolicy: new CustomerFieldPolicy(document, city))
        };

    private static StubQuotationCustomerLookup Customer(
        string? identificationType = "NIT",
        string? identificationNumber = "900123456",
        string? country = "CO",
        Guid? cityId = null,
        string? cityName = null,
        bool unresolvable = false) =>
        new(new QuotationCustomerRef(
            Guid.CreateVersion7(), TenantId, "CUC-001", IsActive: true, "Ferretería El Tornillo",
            "3001234567", "Calle 1 # 2-3", WithRetention: false, VatSurplus: false,
            CityId: cityId, CityName: cityName,
            IdentificationNumber: identificationNumber, Country: country, IdentificationType: identificationType))
        {
            Unresolvable = unresolvable,
        };

    private static Order NewOrder(QuotationParties? parties = null, ShippingDetails? shipping = null) =>
        OrderBuilders.Pending(TenantId, AdvisorId, Now, parties: parties, shipping: shipping);

    private static Task EnsureAsync(StubTenantDirectory directory, StubQuotationCustomerLookup lookup, Order order) =>
        BilledCustomerFieldsRequired.EnsureSatisfiedAsync(
            directory, lookup, TenantId, order, TestContext.Current.CancellationToken);

    private static QuotationPartyDetails Party(string name) =>
        new()
        {
            Name = name,
            IdentificationNumber = "900123456",
            IdentificationType = PartyIdentificationType.Nit,
            Phone = "3001234567",
            Email = "otra@example.com",
            Address = "Calle 1 # 2-3",
            DepartmentId = Guid.CreateVersion7(),
            CityId = Guid.CreateVersion7(),
        };

    [Fact]
    public async Task ACustomerWithoutDocumentPassesWhenTheDocumentIsNotRequiredOnBilling()
    {
        var directory = Directory(NotOnBilling, NotOnBilling);
        var lookup = Customer(identificationType: null, identificationNumber: null);

        await EnsureAsync(directory, lookup, NewOrder());

        Assert.Equal(1, directory.RegionalCalls);
        Assert.Equal(0, lookup.FindCalls);
    }

    [Fact]
    public async Task ACustomerWithoutDocumentIsRejectedWhenRequired()
    {
        var lookup = Customer(identificationType: null, identificationNumber: null);

        var error = await Assert.ThrowsAsync<BilledCustomerFieldRequiredException>(() =>
            EnsureAsync(Directory(OnBilling, NotOnBilling), lookup, NewOrder()));

        Assert.Equal("order.order.billed_document_required", error.Code);
        Assert.Equal("Customer", error.ProblemExtensions["missingOn"]);
    }

    [Fact]
    public async Task ACustomerWithTypeButBlankNumberIsRejectedWhenRequired()
    {
        var error = await Assert.ThrowsAsync<BilledCustomerFieldRequiredException>(() =>
            EnsureAsync(
                Directory(OnBilling, NotOnBilling),
                Customer(identificationType: "NIT", identificationNumber: "  "), NewOrder()));

        Assert.Equal(BilledCustomerFieldsRequired.DocumentCode, error.Code);
    }

    [Fact]
    public async Task AnUnresolvableCustomerCountsAsMissingTheDocument()
    {
        var error = await Assert.ThrowsAsync<BilledCustomerFieldRequiredException>(() =>
            EnsureAsync(Directory(OnBilling, NotOnBilling), Customer(unresolvable: true), NewOrder()));

        Assert.Equal(BilledCustomerFieldsRequired.DocumentCode, error.Code);
    }

    [Fact]
    public async Task AFinalConsumerOrderIsNotChecked()
    {
        var directory = Directory(OnBilling, OnBilling);
        var lookup = Customer(identificationType: null, identificationNumber: null);
        var order = NewOrder(QuotationParties.Empty with { BillsToFinalConsumer = true });

        await EnsureAsync(directory, lookup, order);

        Assert.Equal(0, directory.RegionalCalls);
        Assert.Equal(0, lookup.FindCalls);
    }

    [Fact]
    public async Task ABillingRowIsNotChecked()
    {
        var directory = Directory(OnBilling, OnBilling);
        var lookup = Customer(identificationType: null, identificationNumber: null);
        var order = NewOrder(QuotationParties.Empty with
            {
                Billing = Party("Otra S.A.S."), BillingWithRetention = false, BillingVatSurplus = false,
            });

        await EnsureAsync(directory, lookup, order);

        Assert.Equal(0, directory.RegionalCalls);
        Assert.Equal(0, lookup.FindCalls);
    }

    [Fact]
    public async Task AStorePickupWithoutCityPasses()
    {
        var directory = Directory(NotOnBilling, OnBilling);
        var lookup = Customer();
        var order = NewOrder(QuotationParties.Empty with { IsStorePickup = true });

        await EnsureAsync(directory, lookup, order);

        // La ciudad se exige en facturacion, asi que se leyo al cliente; y aun asi no se la pide.
        Assert.Equal(1, directory.RegionalCalls);
        Assert.Equal(1, lookup.FindCalls);
    }

    [Fact]
    public async Task AnOrderWithoutFreightNeverAsksForTheCity()
    {
        var directory = Directory(NotOnBilling, OnBilling);
        var lookup = Customer();

        await EnsureAsync(directory, lookup, NewOrder(shipping: ShippingDetails.None));

        Assert.Equal(1, directory.RegionalCalls);
        Assert.Equal(1, lookup.FindCalls);
    }

    [Fact]
    public async Task AnOwnShippingRowIsNotCheckedAgainstTheCustomer()
    {
        var directory = Directory(NotOnBilling, OnBilling);
        var lookup = Customer();
        var order = NewOrder(
            QuotationParties.Empty with { Shipping = Party("Bodega destino") }, shipping: Freight);

        await EnsureAsync(directory, lookup, order);

        Assert.Equal(1, directory.RegionalCalls);
        Assert.Equal(1, lookup.FindCalls);
    }

    [Fact]
    public async Task FreightToTheCustomersAddressWithoutCityIsRejected()
    {
        var error = await Assert.ThrowsAsync<BilledCustomerFieldRequiredException>(() =>
            EnsureAsync(Directory(NotOnBilling, OnBilling), Customer(cityId: null), NewOrder(shipping: Freight)));

        Assert.Equal("order.order.shipping_city_required", error.Code);
        Assert.Equal("Customer", error.ProblemExtensions["missingOn"]);
    }

    [Fact]
    public async Task FreightToTheCustomersAddressWithCityPasses()
    {
        var directory = Directory(NotOnBilling, OnBilling);
        var lookup = Customer(cityId: Guid.CreateVersion7());

        await EnsureAsync(directory, lookup, NewOrder(shipping: Freight));

        Assert.Equal(1, directory.RegionalCalls);
        Assert.Equal(1, lookup.FindCalls);
    }

    [Fact]
    public async Task ACustomerWithoutCountryIsTreatedAsColombian()
    {
        // G11: sin pais, la ciudad que vale es el id (DIVIPOLA), no el texto.
        var error = await Assert.ThrowsAsync<BilledCustomerFieldRequiredException>(() =>
            EnsureAsync(
                Directory(NotOnBilling, OnBilling),
                Customer(country: null, cityId: null, cityName: "Bogotá"), NewOrder(shipping: Freight)));

        Assert.Equal(BilledCustomerFieldsRequired.ShippingCityCode, error.Code);
    }

    [Fact]
    public async Task AForeignCustomerNeedsItsCityName()
    {
        var error = await Assert.ThrowsAsync<BilledCustomerFieldRequiredException>(() =>
            EnsureAsync(
                Directory(NotOnBilling, OnBilling),
                Customer(country: "ES", cityId: null, cityName: null), NewOrder(shipping: Freight)));

        Assert.Equal(BilledCustomerFieldsRequired.ShippingCityCode, error.Code);

        var lookup = Customer(country: "ES", cityId: null, cityName: "Madrid");
        await EnsureAsync(Directory(NotOnBilling, OnBilling), lookup, NewOrder(shipping: Freight));
        Assert.Equal(1, lookup.FindCalls);
    }

    [Fact]
    public async Task TheDocumentIsCheckedBeforeTheCity()
    {
        var error = await Assert.ThrowsAsync<BilledCustomerFieldRequiredException>(() =>
            EnsureAsync(
                Directory(OnBilling, OnBilling),
                Customer(identificationNumber: null, cityId: null), NewOrder(shipping: Freight)));

        Assert.Equal(BilledCustomerFieldsRequired.DocumentCode, error.Code);
    }

    [Fact]
    public async Task WithoutRegionalSettingsTheDefaultRules()
    {
        var directory = new StubTenantDirectory { Regional = null };

        var error = await Assert.ThrowsAsync<BilledCustomerFieldRequiredException>(() =>
            EnsureAsync(
                directory, Customer(identificationType: null, identificationNumber: null), NewOrder()));

        Assert.Equal(BilledCustomerFieldsRequired.DocumentCode, error.Code);
        Assert.Equal(1, directory.RegionalCalls);
    }
}
