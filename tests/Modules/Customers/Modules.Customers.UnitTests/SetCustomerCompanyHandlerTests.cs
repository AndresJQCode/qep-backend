using BuildingBlocks.Application;
using Modules.Customers.Application;
using Modules.Customers.Domain;
using Modules.Tenancy.Application;
using static Modules.Customers.UnitTests.CustomerWriteDoubles;

namespace Modules.Customers.UnitTests;

/// <summary>Spec 2026-11-10 contactos de empresa §4.1: <c>PUT /customers/{id}/company</c> sin base. El orden del handler
/// (autorizar, transacción, bloquear, leer, decidir) y la auditoría sólo cuando el vínculo cambia.</summary>
public sealed class SetCustomerCompanyHandlerTests
{
    private static readonly EngagementTestBed.Context Manager =
        new(CustomerWriteDoubles.Tenant, EngagementTestBed.UserA, EngagementTestBed.Manager);

    private static readonly EngagementTestBed.Context ReadOnly =
        new(CustomerWriteDoubles.Tenant, EngagementTestBed.UserA, CustomersPermissions.CustomerRead);

    // Un doble propio de geografía: el de CustomerWriteDoubles no resuelve lotes, y armar la respuesta los pide.
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

    private sealed class Bed
    {
        public Bed(params Customer[] customers)
        {
            Customers.Known.AddRange(customers);
            Directory = new FakeCustomerCompanyDirectory(customers);
        }

        public FakeCustomerRepository Customers { get; } = new();

        public FakeCustomerCompanyDirectory Directory { get; }

        public RecordingAudit Audit { get; } = new();

        public SetCustomerCompanyHandler Handler(IExecutionContext? context = null) =>
            new(Customers, new Classifications([]), new EngagementTestBed.FakeTags(), new BatchGeography(), Directory,
                new UnitOfWork(), Audit, context ?? Manager, new FixedClock());
    }

    private static Customer Complete(string name, string? businessName = null) =>
        Customer.Create(
            CustomerId.New(), CustomerWriteDoubles.Tenant, "CLI08000001", name, businessName, null,
            new CustomerIdentification { Type = IdentificationType.Nit, Number = "900123456" },
            null,
            new CustomerContactInfo { Phone = "3000000000", Email = "v@verde.co", Address = "Calle 1", Country = "CO", CityId = CityId },
            new CustomerCommercialInfo { ClassificationId = Classification.Id },
            Now,
            new PlusNormalizer());

    private static Customer Incomplete() =>
        Customer.CreateIncomplete(
            CustomerId.New(), CustomerWriteDoubles.Tenant, "Alguien", "3000000001", "CO",
            "bsuid-" + Guid.NewGuid().ToString("N"), Now, new PlusNormalizer());

    private static SetCustomerCompanyCommand Command(Customer customer, Customer? company) =>
        new(CustomerWriteDoubles.Tenant, customer.Id.Value, company?.Id.Value);

    [Fact]
    public async Task LinksAndReturnsTheCompanyInTheDto()
    {
        var person = Complete("Laura Pérez");
        var company = Complete("Contacto", businessName: "Verde Esencial S.A.S.");
        var bed = new Bed(person, company);

        var dto = await bed.Handler().HandleAsync(Command(person, company), TestContext.Current.CancellationToken);

        Assert.Equal(new CustomerCompanyDto(company.Id.Value, "Verde Esencial S.A.S.", true, true), dto.Company);
        Assert.Equal(company.Id, person.CompanyId);
    }

    [Fact]
    public async Task CompanyIdNullUnlinks()
    {
        var person = Complete("Laura Pérez");
        var company = Complete("Verde Esencial");
        person.LinkToCompany(company, false, Now);
        var bed = new Bed(person, company);

        var dto = await bed.Handler().HandleAsync(Command(person, null), TestContext.Current.CancellationToken);

        Assert.Null(dto.Company);
        Assert.Null(person.CompanyId);
        Assert.Equal("customers.customer.company_changed", Assert.Single(bed.Audit.Human).Action);
    }

    [Fact]
    public async Task WorksOnAnIncompleteCustomer()
    {
        var person = Incomplete();
        var company = Complete("Verde Esencial");
        var bed = new Bed(person, company);

        var dto = await bed.Handler().HandleAsync(Command(person, company), TestContext.Current.CancellationToken);

        Assert.False(dto.IsComplete);
        Assert.Equal(company.Id.Value, dto.Company?.Id);
    }

    [Fact]
    public async Task TheCustomerMustExist()
    {
        var company = Complete("Verde Esencial");
        var bed = new Bed(company);

        var failure = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            bed.Handler().HandleAsync(
                new SetCustomerCompanyCommand(CustomerWriteDoubles.Tenant, Guid.NewGuid(), company.Id.Value),
                TestContext.Current.CancellationToken));

        Assert.Equal("customers.customer.not_found", failure.Code);
    }

    [Fact]
    public async Task AnUnknownCompanyIsCompanyNotFound()
    {
        var person = Complete("Laura Pérez");
        var bed = new Bed(person);

        var failure = await Assert.ThrowsAsync<CustomersDomainException>(() =>
            bed.Handler().HandleAsync(
                new SetCustomerCompanyCommand(CustomerWriteDoubles.Tenant, person.Id.Value, Guid.NewGuid()),
                TestContext.Current.CancellationToken));

        Assert.Equal("customers.customer.company_not_found", failure.Code);
        Assert.Empty(bed.Audit.Human);
    }

    [Fact]
    public async Task ACustomerWithContactsCannotBecomeAContact()
    {
        var person = Complete("Laura Pérez");
        var company = Complete("Verde Esencial");
        var contactOfPerson = Complete("Pedro Gómez");
        contactOfPerson.LinkToCompany(person, false, Now);
        var bed = new Bed(person, company, contactOfPerson);

        var failure = await Assert.ThrowsAsync<CustomersDomainException>(() =>
            bed.Handler().HandleAsync(Command(person, company), TestContext.Current.CancellationToken));

        Assert.Equal("customers.customer.has_contacts", failure.Code);
        Assert.Null(person.CompanyId);
        Assert.Empty(bed.Audit.Human);
    }

    [Fact]
    public async Task AuthorizesBeforeReadingTheRepository()
    {
        var person = Complete("Laura Pérez");
        var company = Complete("Verde Esencial");
        var bed = new Bed(person, company);

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            bed.Handler(ReadOnly).HandleAsync(Command(person, company), TestContext.Current.CancellationToken));

        Assert.Empty(bed.Customers.Log);
        Assert.Null(person.CompanyId);
    }

    [Fact]
    public async Task PublishesTheAuditOnlyWhenTheLinkChanges()
    {
        var person = Complete("Laura Pérez");
        var company = Complete("Verde Esencial");
        var bed = new Bed(person, company);

        await bed.Handler().HandleAsync(Command(person, company), TestContext.Current.CancellationToken);
        var versionAfterLink = person.Version;
        await bed.Handler().HandleAsync(Command(person, company), TestContext.Current.CancellationToken);

        var audit = Assert.Single(bed.Audit.Human);
        Assert.Equal("customers.customer.company_changed", audit.Action);
        Assert.Equal(person.Id.ToString(), audit.ResourceId);
        Assert.Equal(versionAfterLink, person.Version);
    }

    [Fact]
    public async Task UnlinkingACustomerWithoutCompanyWritesNoAudit()
    {
        var person = Complete("Laura Pérez");
        var bed = new Bed(person);

        var dto = await bed.Handler().HandleAsync(Command(person, null), TestContext.Current.CancellationToken);

        Assert.Null(dto.Company);
        Assert.Empty(bed.Audit.Human);
    }

    [Fact]
    public async Task ReLinkingToAnInactiveCompanyIsANoOp()
    {
        // Ruling P10: repetir el vínculo existente responde igual aunque la empresa se inactivara después.
        var person = Complete("Laura Pérez");
        var company = Complete("Verde Esencial");
        var bed = new Bed(person, company);
        await bed.Handler().HandleAsync(Command(person, company), TestContext.Current.CancellationToken);
        company.Deactivate(Now);

        var dto = await bed.Handler().HandleAsync(Command(person, company), TestContext.Current.CancellationToken);

        Assert.Equal(new CustomerCompanyDto(company.Id.Value, "Verde Esencial", true, false), dto.Company);
        Assert.Single(bed.Audit.Human);
    }

    [Fact]
    public async Task LocksBothRowsBeforeCheckingTheRules()
    {
        var person = Complete("Laura Pérez");
        var company = Complete("Verde Esencial");
        var bed = new Bed(person, company);

        await bed.Handler().HandleAsync(Command(person, company), TestContext.Current.CancellationToken);

        var locked = Assert.Single(bed.Customers.LockCalls);
        Assert.Equivalent(new[] { person.Id.Value, company.Id.Value }, locked);
        Assert.Equal(
            new[] { "lock", "find:" + person.Id.Value, "find:" + company.Id.Value },
            bed.Customers.Log);
    }

    [Fact]
    public async Task TheResponseIsFilledThroughTheSharedBatchPath()
    {
        // Ruling P11: el doble registra cada pedido del lote; armar la respuesta lo pide una sola vez.
        var person = Complete("Laura Pérez");
        var company = Complete("Verde Esencial");
        var bed = new Bed(person, company);

        await bed.Handler().HandleAsync(Command(person, company), TestContext.Current.CancellationToken);

        Assert.Equal([person.Id.Value], Assert.Single(bed.Directory.FindCompaniesCalls));
    }
}
