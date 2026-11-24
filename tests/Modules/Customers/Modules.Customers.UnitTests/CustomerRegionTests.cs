using System.Reflection;
using Modules.Customers.Application;
using Modules.Customers.Domain;
using static Modules.Customers.UnitTests.CustomerWriteDoubles;

namespace Modules.Customers.UnitTests;

/// <summary>
/// La region (provincia) de un domicilio de afuera de Colombia: reglas de escritura, lectura resuelta y las rutas que
/// conservan la ciudad guardada (importacion, completar desde la tienda). Spec 2026-11-23 provincia guardada.
/// </summary>
public sealed class CustomerRegionTests
{
    private static readonly Guid MadridId = Guid.Parse("01900000-0000-7000-8000-0000000000a1");

    private static readonly CustomerRegionRef Madrid = new(MadridId, "ES", "Madrid");

    private static readonly EngagementTestBed.Context Manager =
        new(CustomerWriteDoubles.Tenant, EngagementTestBed.UserA, EngagementTestBed.Manager);

    private static CreateCustomerCommand ForeignCreate(Guid? regionId = null, string country = "ES") =>
        new(CustomerWriteDoubles.Tenant, "Laura Pérez", null, "PASAPORTE", "X1234567", "+34 910 000 000", "laura@verde.es", "Calle Gran Via 28", country, null,
            "Madrid", Classification.Id.Value, false, RegionId: regionId);

    private static CreateCustomerHandler NewCreate(FakeCustomerRepository customers, Geography geography) =>
        new(customers, new Classifications([]), new EngagementTestBed.FakeTags(), new TaxRates(), geography, new FakeCustomerCompanyDirectory([]),
            new UnitOfWork(), new RecordingAudit(), new Cucs([]), Manager, new FixedClock(), new CreateCustomerValidator(), new PlusNormalizer(),
            new TenantDirectory());

    private static Customer Foreign(Guid? regionId = null) =>
        Customer.Create(
            CustomerId.New(), CustomerWriteDoubles.Tenant, "CLI00000001", "Verde Esencial", null, null, null, null,
            new CustomerContactInfo { Phone = "+34910000000", Email = "v@verde.es", Address = "Calle 1", Country = "ES", CityName = "Madrid", RegionId = regionId },
            new CustomerCommercialInfo { ClassificationId = Classification.Id }, Now, new PlusNormalizer());

    private static GetCustomerHandler NewGet(Customer customer, Geography geography)
    {
        var customers = new FakeCustomerRepository();
        customers.Known.Add(customer);
        return new(
            customers, new Classifications([]), new EngagementTestBed.FakeTags(), new TaxRates(), geography,
            new FakeCustomerCompanyDirectory([customer]), Manager);
    }

    [Theory]
    [InlineData("co")]
    [InlineData("")]
    public void ColombianCommandWithRegionFailsValidation(string country)
    {
        var result = new CreateCustomerValidator().Validate(ForeignCreate(MadridId, country));

        Assert.Contains(result.Errors, error => error.PropertyName == "RegionId");
    }

    [Fact]
    public void ColombianAddressCommandWithRegionFailsValidation()
    {
        var command = new AddCustomerAddressCommand(
            CustomerWriteDoubles.Tenant, Guid.NewGuid(), "Bodega", "Calle 1", CityId, null, false, RegionId: MadridId);

        var result = new AddCustomerAddressValidator().Validate(command);

        Assert.Contains(result.Errors, error => error.PropertyName == "RegionId");
    }

    [Fact]
    public async Task ForeignCreateStoresTheRegion()
    {
        var customers = new FakeCustomerRepository();

        await NewCreate(customers, new Geography([], Madrid)).HandleAsync(ForeignCreate(MadridId), TestContext.Current.CancellationToken);

        Assert.Equal(MadridId, Assert.Single(customers.Added).RegionId);
    }

    [Fact]
    public async Task RegionOfAnotherCountryIsRejected()
    {
        var customers = new FakeCustomerRepository();

        var failure = await Assert.ThrowsAsync<CustomersDomainException>(() =>
            NewCreate(customers, new Geography([], Madrid)).HandleAsync(ForeignCreate(MadridId, "FR"), TestContext.Current.CancellationToken));

        Assert.Equal("customers.customer.region_invalid", failure.Code);
        Assert.Equal(["RegionId"], failure.FieldErrors.Keys);
        Assert.Empty(customers.Added);
    }

    [Fact]
    public async Task DepartmentIdAsRegionIsRejected()
    {
        var customers = new FakeCustomerRepository();

        var failure = await Assert.ThrowsAsync<CustomersDomainException>(() =>
            NewCreate(customers, new Geography([], Madrid)).HandleAsync(ForeignCreate(Guid.CreateVersion7()), TestContext.Current.CancellationToken));

        Assert.Equal("customers.customer.region_invalid", failure.Code);
    }

    [Fact]
    public async Task AddressRegionOfAnotherCountryIsRejected()
    {
        var customer = Foreign();
        var customers = new FakeCustomerRepository();
        customers.Known.Add(customer);
        var handler = new AddCustomerAddressHandler(
            customers, new Classifications([]), new EngagementTestBed.FakeTags(), new TaxRates(), new Geography([], Madrid),
            new FakeCustomerCompanyDirectory([customer]), new UnitOfWork(), Manager, new FixedClock(), new AddCustomerAddressValidator());

        var failure = await Assert.ThrowsAsync<CustomersDomainException>(() => handler.HandleAsync(
            new AddCustomerAddressCommand(
                CustomerWriteDoubles.Tenant, customer.Id.Value, "Sede", "Calle 2", null, null, false, "FR", "Paris", MadridId),
            TestContext.Current.CancellationToken));

        Assert.Equal("customers.address.region_invalid", failure.Code);
    }

    [Fact]
    public async Task ForeignCustomerDetailCarriesRegion()
    {
        var customer = Foreign(MadridId);
        customer.AddAddress(
            new CustomerAddressDetails { Name = "Sede", Address = "Calle 2", Country = "ES", CityName = "Alcalá", RegionId = MadridId }, false, Now);
        var geography = new Geography([], Madrid);

        var dto = await NewGet(customer, geography).HandleAsync(
            new GetCustomerQuery(CustomerWriteDoubles.Tenant, customer.Id.Value), TestContext.Current.CancellationToken);

        Assert.Equal(new CustomerRegionDto(MadridId, "Madrid"), dto.Region);
        Assert.Equal(new CustomerRegionDto(MadridId, "Madrid"), Assert.Single(dto.Addresses).Region);
        Assert.Single(geography.RegionCalls);
    }

    [Fact]
    public async Task CustomerWithoutRegionHasNullRegion()
    {
        var customer = Foreign();

        var dto = await NewGet(customer, new Geography([], Madrid)).HandleAsync(
            new GetCustomerQuery(CustomerWriteDoubles.Tenant, customer.Id.Value), TestContext.Current.CancellationToken);

        Assert.Null(dto.Region);
    }

    // Regla de conservacion: toda ruta que deja el CityName guardado deja tambien la region. Una fila de Excel sin departamento
    // ni ciudad conserva el carril de ciudad del cliente.
    [Fact]
    public async Task ImportRowWithoutCityKeepsRegion()
    {
        var customer = Foreign(MadridId);
        var repository = new FakeCustomerRepository();
        repository.Known.Add(customer);
        var row = new ExcelCustomerRow(
            2, customer.Cuc, "Verde Esencial 2", null, null, null, "+34910000000", null, "Calle 2", null, null, null, null, "No");
        var handler = new ImportCustomersHandler(
            repository, new FakeCustomerCompanyDirectory([customer]), new EngagementTestBed.FakeTags(), new Classifications([]),
            new Geography([], Madrid), new TaxRates(), new SequentialCucs(), new StubImporter(row),
            new UnitOfWork(), new RecordingAudit(),
            new EngagementTestBed.Context(CustomerWriteDoubles.Tenant, EngagementTestBed.UserA, CustomersPermissions.CustomerImport),
            new FixedClock(), new TenantDirectory(), new PlusNormalizer());

        var response = await handler.HandleAsync(
            new ImportCustomersCommand(CustomerWriteDoubles.Tenant, "clientes.xlsx", 10, new MemoryStream([1])), CancellationToken.None);

        Assert.Empty(response.Errors);
        Assert.Equal("Verde Esencial 2", customer.Name);
        Assert.Equal(("ES", "Madrid", MadridId), (customer.Country, customer.CityName, customer.RegionId));
    }

    // Completar desde la tienda con el pais y la ciudad que el cliente ya tenia: la region sigue ahi. Un incompleto no llega a
    // tener ciudad por el dominio, asi que la ciudad y la region se siembran por reflexion, como lo hace la materializacion de EF.
    [Fact]
    public async Task StoreMergeKeepingCityKeepsRegion()
    {
        var customer = Customer.CreateIncompleteFromExternalSale(
            CustomerId.New(), CustomerWriteDoubles.Tenant, "Laura", "+34612345678", null, "ES", Now, new PlusNormalizer());
        Seed(customer, nameof(Customer.CityName), "Madrid");
        Seed(customer, nameof(Customer.RegionId), MadridId);
        var repository = new FakeCustomerRepository { Existing = customer };
        var provisioning = new CustomerProvisioning(
            repository, new Classifications([]), new Geography([]), new UnitOfWork(), new RecordingAudit(), new Cucs([]),
            new FixedClock(), new CreateCustomerValidator(), new PlusNormalizer(), new TenantDirectory());

        await provisioning.CompleteFromStoreAsync(
            CustomerWriteDoubles.Tenant,
            customer.Id.Value,
            new CompleteCustomerData(
                "Laura", "NIF", "12345678Z", "+34612345678", "laura@verde.es", "Calle Mayor 1", "ES", null, "Barcelona", Classification.Id.Value),
            Guid.CreateVersion7(),
            TestContext.Current.CancellationToken);

        Assert.True(customer.IsComplete);
        Assert.Equal(("Madrid", MadridId), (customer.CityName, customer.RegionId));
    }

    // The domain allows a region without a city: a store merge keeps the customer's own region even when the customer has no
    // city text and the store brings one. The region is the customer's province; the store only fills the missing city.
    [Fact]
    public async Task StoreMergeKeepsRegionOfACustomerWithoutCityName()
    {
        var customer = Customer.CreateIncompleteFromExternalSale(
            CustomerId.New(), CustomerWriteDoubles.Tenant, "Laura", "+34612345678", null, "ES", Now, new PlusNormalizer());
        Seed(customer, nameof(Customer.RegionId), MadridId);
        var repository = new FakeCustomerRepository { Existing = customer };
        var provisioning = new CustomerProvisioning(
            repository, new Classifications([]), new Geography([]), new UnitOfWork(), new RecordingAudit(), new Cucs([]),
            new FixedClock(), new CreateCustomerValidator(), new PlusNormalizer(), new TenantDirectory());

        await provisioning.CompleteFromStoreAsync(
            CustomerWriteDoubles.Tenant,
            customer.Id.Value,
            new CompleteCustomerData(
                "Laura", "NIF", "12345678Z", "+34612345678", "laura@verde.es", "Calle Mayor 1", "ES", null, "Barcelona", Classification.Id.Value),
            Guid.CreateVersion7(),
            TestContext.Current.CancellationToken);

        Assert.True(customer.IsComplete);
        Assert.Equal(("Barcelona", MadridId), (customer.CityName, customer.RegionId));
    }

    private static void Seed(Customer customer, string property, object value) =>
        typeof(Customer).GetProperty(property, BindingFlags.Public | BindingFlags.Instance)!.SetValue(customer, value);

    private sealed class StubImporter(params ExcelCustomerRow[] rows) : IExcelCustomerImporter
    {
        public ExcelCustomerImportFile Parse(Stream content, CancellationToken cancellationToken) => new(true, rows);
    }

    private sealed class SequentialCucs : ICucGenerator
    {
        public Task<long> NextAsync(Guid tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<long> NextBatchAsync(Guid tenantId, int count, CancellationToken cancellationToken) => Task.FromResult(100L);
    }
}
