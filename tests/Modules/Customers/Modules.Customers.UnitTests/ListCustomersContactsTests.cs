using FluentValidation;
using Modules.Customers.Application;
using Modules.Customers.Domain;
using static Modules.Customers.UnitTests.CustomerWriteDoubles;

namespace Modules.Customers.UnitTests;

/// <summary>Spec 2026-11-10 contactos de empresa §5: qué decide el listado sobre los contactos (<c>contacts</c> y las cajas de
/// búsqueda), y que cada fila trae su empresa y su conteo resueltos en lote para toda la página.</summary>
public sealed class ListCustomersContactsTests
{
    private static readonly EngagementTestBed.Context Reader =
        new(CustomerWriteDoubles.Tenant, EngagementTestBed.UserA, CustomersPermissions.CustomerRead);

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

    private sealed class BatchClassifications : IClientClassificationRepository
    {
        public Task<ClientClassification?> FindAsync(Guid tenantId, ClientClassificationId classificationId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ClientClassification>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ClientClassification>> ListByIdsAsync(
            Guid tenantId, IReadOnlyCollection<ClientClassificationId> classificationIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ClientClassification>>(
                classificationIds.Contains(Classification.Id) ? [Classification] : []);

        public Task<ClientClassification?> FindByNameAsync(Guid tenantId, string name, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void Add(ClientClassification classification) => throw new NotSupportedException();

        public void Remove(ClientClassification classification) => throw new NotSupportedException();
    }

    private static Customer Complete(string name, string? businessName = null) =>
        Customer.Create(
            CustomerId.New(), CustomerWriteDoubles.Tenant, "CLI08000001", name, businessName, null,
            new CustomerIdentification { Type = IdentificationType.Nit, Number = "900" + Guid.NewGuid().ToString("N")[..6] },
            null,
            new CustomerContactInfo { Phone = "3000000000", Email = "v@verde.co", Address = "Calle 1", Country = "CO", CityId = CityId },
            new CustomerCommercialInfo { ClassificationId = Classification.Id },
            Now,
            new PlusNormalizer());

    private sealed class Bed
    {
        public Bed(params Customer[] customers)
        {
            Repository.SearchPage.AddRange(customers);
            Directory = new FakeCustomerCompanyDirectory(customers);
        }

        public FakeCustomerRepository Repository { get; } = new();

        public FakeCustomerCompanyDirectory Directory { get; }

        public ListCustomersHandler Handler() =>
            new(Repository, new BatchClassifications(), new BatchGeography(), new CustomerWriteDoubles.TaxRates(), Directory, Reader, new ListCustomersValidator());
    }

    private static ListCustomersQuery Query(
        string? contacts = null, string? search = null, string? name = null, string? identificationNumber = null,
        string? cuc = null, string? phone = null) =>
        new(CustomerWriteDoubles.Tenant, search, name, identificationNumber, cuc, null, null, 1, 50, null, phone, contacts);

    [Theory]
    [InlineData(null, null, false)]
    [InlineData(null, "acme", true)]
    [InlineData(null, "   ", false)]
    [InlineData(null, "", false)]
    [InlineData("include", null, true)]
    [InlineData("exclude", "acme", false)]
    [InlineData("INCLUDE", null, true)]
    [InlineData("Exclude", "acme", false)]
    public async Task IncludeContactsIsDecidedFromTheParameters(string? contacts, string? search, bool expected)
    {
        var bed = new Bed();

        await bed.Handler().HandleAsync(Query(contacts, search), TestContext.Current.CancellationToken);

        Assert.Equal(expected, Assert.Single(bed.Repository.SearchCalls));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("identificationNumber")]
    [InlineData("cuc")]
    [InlineData("phone")]
    public async Task AnySearchBoxUnhidesContacts(string box)
    {
        var bed = new Bed();
        var query = box switch
        {
            "name" => Query(name: "acme"),
            "identificationNumber" => Query(identificationNumber: "900"),
            "cuc" => Query(cuc: "CLI"),
            _ => Query(phone: "3001234567")
        };

        await bed.Handler().HandleAsync(query, TestContext.Current.CancellationToken);

        Assert.True(Assert.Single(bed.Repository.SearchCalls));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("identificationNumber")]
    [InlineData("cuc")]
    [InlineData("phone")]
    public async Task ABlankOrEmptyBoxIsNotASearch(string box)
    {
        var bed = new Bed();

        foreach (var blank in new[] { "", "   " })
        {
            var query = box switch
            {
                "name" => Query(name: blank),
                "identificationNumber" => Query(identificationNumber: blank),
                "cuc" => Query(cuc: blank),
                _ => Query(phone: blank)
            };
            await bed.Handler().HandleAsync(query, TestContext.Current.CancellationToken);
        }

        Assert.All(bed.Repository.SearchCalls, included => Assert.False(included));
    }

    [Fact]
    public async Task APhoneBoxWithoutDigitsIsNotASearch()
    {
        // El repositorio no filtra por un teléfono sin dígitos (PhoneFilterPattern): tampoco puede destapar a los contactos.
        var bed = new Bed();

        await bed.Handler().HandleAsync(Query(phone: "abc"), TestContext.Current.CancellationToken);

        Assert.False(Assert.Single(bed.Repository.SearchCalls));
    }

    [Theory]
    [InlineData("all")]
    [InlineData("")]
    [InlineData("true")]
    public async Task AnUnknownContactsValueFailsValidationWithItsKey(string contacts)
    {
        var bed = new Bed();

        var failure = await Assert.ThrowsAsync<ValidationException>(() =>
            bed.Handler().HandleAsync(Query(contacts), TestContext.Current.CancellationToken));

        Assert.Contains(failure.Errors, error => error.PropertyName == "contacts");
        Assert.Empty(bed.Repository.SearchCalls);
    }

    [Fact]
    public async Task ThePageCarriesCompanyAndContactCountPerItem()
    {
        var company = Complete("Contacto Acme", businessName: "Acme S.A.S.");
        var otherCompany = Complete("Otra Empresa");
        var contactA = Complete("Ana Ruiz");
        var contactB = Complete("Beto Díaz");
        contactA.LinkToCompany(company, false, Now);
        contactB.LinkToCompany(company, false, Now);
        var alone = Complete("Sin Vínculo");
        var bed = new Bed(company, otherCompany, contactA, contactB, alone);

        var page = await bed.Handler().HandleAsync(Query(contacts: "include"), TestContext.Current.CancellationToken);

        var byId = page.Items.ToDictionary(item => item.Id);
        Assert.Equal(2, byId[company.Id.Value].ContactCount);
        Assert.Equal(0, byId[otherCompany.Id.Value].ContactCount);
        Assert.Equal(0, byId[contactA.Id.Value].ContactCount);
        Assert.Null(byId[company.Id.Value].Company);
        Assert.Equal("Acme S.A.S.", byId[contactA.Id.Value].Company!.Name);
        Assert.Equal(company.Id.Value, byId[contactB.Id.Value].Company!.Id);
        Assert.Null(byId[alone.Id.Value].Company);
        Assert.Equal("Acme S.A.S.", byId[company.Id.Value].BusinessName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AContactsCompanyWithoutABusinessNameIsLabelledByItsName(string? businessName)
    {
        var company = Complete("Verde Esencial", businessName);
        var contact = Complete("Laura Pérez");
        contact.LinkToCompany(company, false, Now);
        var bed = new Bed(company, contact);

        var page = await bed.Handler().HandleAsync(Query(contacts: "include"), TestContext.Current.CancellationToken);

        var item = page.Items.Single(candidate => candidate.Id == contact.Id.Value);
        Assert.Equal("Verde Esencial", item.Company!.Name);
    }

    [Fact]
    public async Task ADeactivatedCompanyStillTravelsWithTheContactsReference()
    {
        var company = Complete("Verde Esencial", "Verde S.A.S.");
        var contact = Complete("Laura Pérez");
        contact.LinkToCompany(company, false, Now);
        company.Deactivate(Now);
        var bed = new Bed(company, contact);

        var page = await bed.Handler().HandleAsync(Query(contacts: "include"), TestContext.Current.CancellationToken);

        var reference = page.Items.Single(candidate => candidate.Id == contact.Id.Value).Company!;
        Assert.Equal(company.Id.Value, reference.Id);
        Assert.False(reference.IsActive);
    }

    [Fact]
    public async Task ThePageResolvesCompaniesAndCountsWithOneBatchCallNoMatterTheRowCount()
    {
        var one = new Bed(Complete("Una"));
        var many = new Bed(Enumerable.Range(0, 20).Select(index => Complete("Cliente " + index)).ToArray());

        await one.Handler().HandleAsync(Query(contacts: "include"), TestContext.Current.CancellationToken);
        await many.Handler().HandleAsync(Query(contacts: "include"), TestContext.Current.CancellationToken);

        // Una llamada por página, con todos los ids de la página: no una por fila.
        Assert.Single(one.Directory.FindCompaniesCalls);
        var batch = Assert.Single(many.Directory.FindCompaniesCalls);
        Assert.Equal(20, batch.Count);
    }
}
