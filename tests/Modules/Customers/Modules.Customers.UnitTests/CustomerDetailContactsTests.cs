using Modules.Customers.Application;
using Modules.Customers.Domain;
using static Modules.Customers.UnitTests.CustomerWriteDoubles;

namespace Modules.Customers.UnitTests;

/// <summary>Spec 2026-11-10 §4.2 (ruling P4): los contactos y su conteo se llenan en <c>CustomerMapping.ToDtoAsync</c>, el camino
/// que comparten todos los handlers que responden la ficha. Se prueban por el GET y por una escritura (el vínculo).</summary>
public sealed class CustomerDetailContactsTests
{
    private static readonly EngagementTestBed.Context Manager =
        new(CustomerWriteDoubles.Tenant, EngagementTestBed.UserA, EngagementTestBed.Manager);

    private sealed class BatchGeography : ICustomerGeographyLookup
    {
        public Task<CustomerCityRef?> FindCityAsync(Guid cityId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<Guid, CustomerCityRef>> FindCitiesAsync(
            IReadOnlyCollection<Guid> cityIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, CustomerCityRef>>(
                cityIds.Contains(CityId)
                    ? new Dictionary<Guid, CustomerCityRef>
                    {
                        [CityId] = new(CityId, "08001", "Barranquilla", Guid.NewGuid(), "08", "Atlántico")
                    }
                    : new Dictionary<Guid, CustomerCityRef>());

        public Task<CustomerCityRef?> FindCityByNameAsync(string departmentName, string cityName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CustomerDepartmentDto>> ListDepartmentsAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CustomerDepartmentWithCitiesDto>> ListDepartmentsWithCitiesAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyCollection<Guid>> ListCityIdsByDepartmentsAsync(IReadOnlyCollection<Guid> departmentIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static Customer Complete(string name) =>
        Customer.Create(
            CustomerId.New(), CustomerWriteDoubles.Tenant, "CLI08000001", name, null, null,
            new CustomerIdentification { Type = IdentificationType.Nit, Number = "900123456" },
            null,
            new CustomerContactInfo { Phone = "3000000000", Email = "v@verde.co", Address = "Calle 1", Country = "CO", CityId = CityId },
            new CustomerCommercialInfo { ClassificationId = Classification.Id },
            Now,
            new PlusNormalizer());

    private static GetCustomerHandler Get(FakeCustomerRepository repository, params Customer[] all)
    {
        repository.Known.AddRange(all);
        return new(
            repository, new Classifications([]), new EngagementTestBed.FakeTags(), new CustomerWriteDoubles.TaxRates(), new BatchGeography(),
            new FakeCustomerCompanyDirectory(all), Manager);
    }

    [Fact]
    public async Task TheCompanyDetailCarriesItsContactsOrderedByNameAndTheCount()
    {
        var company = Complete("Verde Esencial");
        var beto = Complete("Beto");
        var ana = Complete("Ana");
        beto.LinkToCompany(company, false, Now);
        ana.LinkToCompany(company, false, Now);

        var dto = await Get(new FakeCustomerRepository(), company, beto, ana).HandleAsync(
            new GetCustomerQuery(CustomerWriteDoubles.Tenant, company.Id.Value), TestContext.Current.CancellationToken);

        Assert.Equal(2, dto.ContactCount);
        Assert.Equal(["Ana", "Beto"], dto.Contacts!.Select(contact => contact.Name));
        Assert.Null(dto.Company);
    }

    [Fact]
    public async Task AContactsDetailCarriesItsCompanyAndNoContacts()
    {
        var company = Complete("Verde Esencial");
        var contact = Complete("Ana");
        contact.LinkToCompany(company, false, Now);

        var dto = await Get(new FakeCustomerRepository(), company, contact).HandleAsync(
            new GetCustomerQuery(CustomerWriteDoubles.Tenant, contact.Id.Value), TestContext.Current.CancellationToken);

        Assert.Equal(company.Id.Value, dto.Company!.Id);
        Assert.Empty(dto.Contacts!);
        Assert.Equal(0, dto.ContactCount);
    }

    [Fact]
    public async Task TheDetailCapsContactsAtOneHundredAndStillCountsThemAll()
    {
        var company = Complete("Verde Esencial");
        var contacts = Enumerable.Range(0, CustomerContactsLimits.MaxInDetail + 1)
            .Select(index => Complete(FormattableString.Invariant($"Contacto {index:D3}")))
            .ToArray();
        foreach (var contact in contacts)
        {
            contact.LinkToCompany(company, false, Now);
        }

        var dto = await Get(new FakeCustomerRepository(), [company, .. contacts]).HandleAsync(
            new GetCustomerQuery(CustomerWriteDoubles.Tenant, company.Id.Value), TestContext.Current.CancellationToken);

        Assert.Equal(100, dto.Contacts!.Count);
        Assert.Equal(101, dto.ContactCount);
    }

    [Fact]
    public async Task AWriteAnsweringTheCompanyAlsoCarriesItsContacts()
    {
        // Ruling P4: ningún endpoint responde `contacts: []` para una empresa que los tiene. Vincular a una persona a otra
        // empresa responde la ficha de la persona; aquí se ejerce el camino compartido con una empresa que ya tiene contactos.
        var company = Complete("Verde Esencial");
        var existing = Complete("Ana");
        existing.LinkToCompany(company, false, Now);
        var repository = new FakeCustomerRepository();
        repository.Known.AddRange([company, existing]);
        var directory = new FakeCustomerCompanyDirectory([company, existing]);
        var deactivate = new DeactivateCustomerHandler(
            repository, new Classifications([]), new EngagementTestBed.FakeTags(), new CustomerWriteDoubles.TaxRates(), new BatchGeography(), directory,
            new UnitOfWork(), new RecordingAudit(), Manager, new FixedClock());

        var dto = await deactivate.HandleAsync(
            new DeactivateCustomerCommand(CustomerWriteDoubles.Tenant, company.Id.Value), TestContext.Current.CancellationToken);

        Assert.Equal(1, dto.ContactCount);
        Assert.Equal("Ana", Assert.Single(dto.Contacts!).Name);
    }
}
