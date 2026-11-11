using Modules.Customers.Application;
using Modules.Customers.Domain;
using static Modules.Customers.UnitTests.CustomerWriteDoubles;

namespace Modules.Customers.UnitTests;

/// <summary>Spec 2026-11-10 §4.8: la columna «Empresa» de la importación, sin base. El handler recibe filas ya leídas (el
/// lector del Excel lo prueba <c>ClosedXmlCustomerImporter</c> aparte) y el doble del repositorio conoce a los clientes que
/// cada prueba arma. Lo que se mira es lo que sale: qué filas se escriben, a quién queda vinculado cada uno y el error de las
/// que no.</summary>
public sealed class ImportCustomersCompanyTests
{
    private const string Field = "companyIdentificationNumber";

    private static readonly EngagementTestBed.Context Importer =
        new(CustomerWriteDoubles.Tenant, EngagementTestBed.UserA, CustomersPermissions.CustomerImport);

    private sealed class StubImporter(params ExcelCustomerRow[] rows) : IExcelCustomerImporter
    {
        public ExcelCustomerImportFile Parse(Stream content, CancellationToken cancellationToken) => new(true, rows);
    }

    private sealed class ColombiaGeography : ICustomerGeographyLookup
    {
        public Task<CustomerCityRef?> FindCityAsync(Guid cityId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<Guid, CustomerCityRef>> FindCitiesAsync(IReadOnlyCollection<Guid> cityIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CustomerCityRef?> FindCityByNameAsync(string departmentName, string cityName, CancellationToken cancellationToken) =>
            Task.FromResult<CustomerCityRef?>(
                new CustomerCityRef(CustomerWriteDoubles.CityId, "08001", "Barranquilla", Guid.NewGuid(), "08", "Atlantico"));

        public Task<IReadOnlyList<CustomerDepartmentDto>> ListDepartmentsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<CustomerDepartmentWithCitiesDto>> ListDepartmentsWithCitiesAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyCollection<Guid>> ListCityIdsByDepartmentsAsync(IReadOnlyCollection<Guid> departmentIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class SequentialCucs : ICucGenerator
    {
        public Task<long> NextAsync(Guid tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<long> NextBatchAsync(Guid tenantId, int count, CancellationToken cancellationToken) => Task.FromResult(100L);
    }

    /// <summary>Una transacción que recuerda si se confirmó: lo que importa de ella es que el bloqueo viva hasta el final.</summary>
    private sealed class TrackedUnitOfWork : ICustomersUnitOfWork
    {
        public int Saves { get; private set; }

        public int Commits { get; private set; }

        public int Begins { get; private set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
        {
            Saves++;
            return Task.FromResult(1);
        }

        public Task<ICustomersTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
        {
            Begins++;
            return Task.FromResult<ICustomersTransaction>(new Transaction(this));
        }

        private sealed class Transaction(TrackedUnitOfWork owner) : ICustomersTransaction
        {
            public Task CommitAsync(CancellationToken cancellationToken)
            {
                owner.Commits++;
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class Bed
    {
        public Bed(params Customer[] existing)
        {
            Repository.Known.AddRange(existing);
            Directory = new FakeCustomerCompanyDirectory(existing);
        }

        public FakeCustomerRepository Repository { get; } = new();

        public FakeCustomerCompanyDirectory Directory { get; }

        public TrackedUnitOfWork Work { get; } = new();

        public async Task<ImportCustomersResponse> ImportAsync(params ExcelCustomerRow[] rows)
        {
            var handler = new ImportCustomersHandler(
                Repository, Directory, new Classifications([]), new ColombiaGeography(), new SequentialCucs(), new StubImporter(rows),
                Work, new RecordingAudit(), Importer, new FixedClock(), new TenantDirectory(), new PlusNormalizer());
            return await handler.HandleAsync(
                new ImportCustomersCommand(CustomerWriteDoubles.Tenant, "clientes.xlsx", 10, new MemoryStream([1])), CancellationToken.None);
        }
    }

    private static Customer Existing(string cuc, string name, string number, IdentificationType type = IdentificationType.Nit) =>
        Customer.Create(
            CustomerId.New(), CustomerWriteDoubles.Tenant, cuc, name, null, null,
            new CustomerIdentification { Type = type, Number = number }, null,
            new CustomerContactInfo { Phone = "3000000000", Email = "v@verde.co", Address = "Calle 1", Country = "CO", CityId = CustomerWriteDoubles.CityId },
            new CustomerCommercialInfo(), CustomerWriteDoubles.Now, new PlusNormalizer());

    private static ExcelCustomerRow Row(
        int rowNumber, string name, string number, string? company = null, string? cuc = null, string type = "NIT") =>
        new(rowNumber, cuc, name, null, type, number, "3001112233", null, "Calle 2", "Atlantico", "Barranquilla", null, "No", "No", company);

    private static ImportRowError ErrorOf(ImportCustomersResponse response, int rowNumber) =>
        Assert.Single(response.Errors, error => error.RowNumber == rowNumber);

    private static Customer Created(Bed bed, string name) => Assert.Single(bed.Repository.Added, customer => customer.Name == name);

    [Fact]
    public async Task ARowIsLinkedToAnExistingCompanyByItsDocumentNumber()
    {
        var company = Existing("CLI08000001", "Verde SAS", "900111111");
        var bed = new Bed(company);

        var response = await bed.ImportAsync(Row(2, "Ana", "1010", "900111111", type: "CC"));

        Assert.Empty(response.Errors);
        Assert.Equal(company.Id, Created(bed, "Ana").CompanyId);
        // El vínculo toma los mismos bloqueos que la acción de vincular y vive en una transacción que se confirma.
        Assert.Contains(bed.Repository.LockCalls, ids => ids.Contains(company.Id.Value));
        Assert.Equal(1, bed.Work.Begins);
        Assert.Equal(1, bed.Work.Commits);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ARowIsLinkedToACompanyThatComesInTheSameFile(bool companyRowIsAbove)
    {
        var bed = new Bed();
        var companyRow = Row(companyRowIsAbove ? 2 : 3, "Verde SAS", "900111111");
        var contactRow = Row(companyRowIsAbove ? 3 : 2, "Ana", "1010", "900111111", type: "CC");

        var response = await bed.ImportAsync(companyRowIsAbove ? [companyRow, contactRow] : [contactRow, companyRow]);

        Assert.Empty(response.Errors);
        Assert.Equal(2, response.ImportedCount);
        Assert.Equal(Created(bed, "Verde SAS").Id, Created(bed, "Ana").CompanyId);
        Assert.Null(Created(bed, "Verde SAS").CompanyId);
    }

    [Fact]
    public async Task AnUnknownNumberIsARowError()
    {
        var bed = new Bed();

        var response = await bed.ImportAsync(Row(2, "Ana", "1010", "999999999", type: "CC"));

        var error = ErrorOf(response, 2);
        Assert.Equal("customers.import.row.company_not_found", error.Code);
        Assert.Equal(Field, error.Field);
        Assert.Equal("999999999", error.RowData!.CompanyIdentificationNumber);
        Assert.Empty(bed.Repository.Added);
    }

    [Fact]
    public async Task ANumberOwnedByTwoCustomersIsAmbiguous()
    {
        var bed = new Bed(
            Existing("CLI08000001", "Verde SAS", "900111111"),
            Existing("CLI08000002", "Verde Persona", "900111111", IdentificationType.Cc));

        var response = await bed.ImportAsync(Row(2, "Ana", "1010", "900111111", type: "CC"));

        var error = ErrorOf(response, 2);
        Assert.Equal("customers.import.row.company_ambiguous", error.Code);
        Assert.Equal(Field, error.Field);
        Assert.Empty(bed.Repository.Added);
    }

    [Theory]
    [InlineData("inactive", "customers.import.row.company_inactive")]
    [InlineData("self", "customers.import.row.company_is_self")]
    [InlineData("contact", "customers.import.row.company_is_contact")]
    [InlineData("has_contacts", "customers.import.row.has_contacts")]
    public async Task ARuleBrokenIsARowErrorWithTheMatchingSuffix(string scenario, string code)
    {
        var company = Existing("CLI08000001", "Verde SAS", "900111111");
        var other = Existing("CLI08000002", "Otra SAS", "900222222");
        var someone = Existing("CLI08000003", "Alguien", "3003", IdentificationType.Cc);
        ExcelCustomerRow row;
        Bed bed;
        switch (scenario)
        {
            case "inactive":
                company.Deactivate(CustomerWriteDoubles.Now);
                bed = new Bed(company);
                row = Row(2, "Ana", "1010", "900111111", type: "CC");
                break;
            case "self":
                bed = new Bed(company);
                row = Row(2, "Ana", "1010", "1010", type: "CC");
                break;
            case "contact":
                // La empresa nombrada ya es contacto de otra: dos niveles.
                company.LinkToCompany(other, false, CustomerWriteDoubles.Now);
                bed = new Bed(company, other);
                row = Row(2, "Ana", "1010", "900111111", type: "CC");
                break;
            default:
                // El cliente que la fila actualiza ya tiene contactos: no puede volverse contacto.
                someone.LinkToCompany(company, false, CustomerWriteDoubles.Now);
                bed = new Bed(company, other, someone);
                row = Row(2, "Verde SAS", "900111111", "900222222", cuc: "CLI08000001");
                break;
        }

        var response = await bed.ImportAsync(row);

        var error = ErrorOf(response, 2);
        Assert.Equal(code, error.Code);
        Assert.Equal(Field, error.Field);
        Assert.Empty(bed.Repository.Added);
        Assert.Equal(0, response.ImportedCount);
    }

    [Fact]
    public async Task TwoRowsNamingEachOtherAreBothRejected()
    {
        var bed = new Bed();

        var response = await bed.ImportAsync(
            Row(2, "Ana", "1010", "2020", type: "CC"),
            Row(3, "Beto", "2020", "1010", type: "CC"));

        Assert.Equal("customers.import.row.company_is_contact", ErrorOf(response, 2).Code);
        Assert.Equal("customers.import.row.company_is_contact", ErrorOf(response, 3).Code);
        Assert.Equal(Field, ErrorOf(response, 2).Field);
        // Ninguna quedó a medias: ni vinculada ni escrita.
        Assert.Empty(bed.Repository.Added);
        Assert.Equal(0, response.ImportedCount);
    }

    [Fact]
    public async Task ARowNamingItsOwnDocumentIsRejectedAndTheRestOfTheFileGoesThrough()
    {
        var bed = new Bed();

        var response = await bed.ImportAsync(
            Row(2, "Ana", "1010", "1010", type: "CC"),
            Row(3, "Verde SAS", "900111111"));

        Assert.Equal("customers.import.row.company_is_self", ErrorOf(response, 2).Code);
        Assert.Equal(["Verde SAS"], bed.Repository.Added.Select(customer => customer.Name));
    }

    [Fact]
    public async Task AChainOfThreeLinksTheFirstContactAndRejectsTheOneThatNamesIt()
    {
        var bed = new Bed();

        // C nombra a X como su empresa; Z nombra a C. Un solo nivel: C queda contacto de X, y Z no puede tener por empresa a un contacto.
        var response = await bed.ImportAsync(
            Row(2, "X SAS", "900111111"),
            Row(3, "C", "1010", "900111111", type: "CC"),
            Row(4, "Z", "2020", "1010", type: "CC"));

        Assert.Equal("customers.import.row.company_is_contact", ErrorOf(response, 4).Code);
        Assert.Equal(Created(bed, "X SAS").Id, Created(bed, "C").CompanyId);
        Assert.DoesNotContain(bed.Repository.Added, customer => customer.Name == "Z");
    }

    [Fact]
    public async Task AnEmptyCompanyCellKeepsAnExistingLink()
    {
        var company = Existing("CLI08000001", "Verde SAS", "900111111");
        var contact = Existing("CLI08000002", "Ana", "1010", IdentificationType.Cc);
        contact.LinkToCompany(company, false, CustomerWriteDoubles.Now);
        var bed = new Bed(company, contact);

        var response = await bed.ImportAsync(Row(2, "Ana Maria", "1010", company: null, cuc: "CLI08000002", type: "CC"));

        Assert.Empty(response.Errors);
        Assert.Equal("updated", Assert.Single(response.Imported).Action);
        Assert.Equal("Ana Maria", contact.Name);
        Assert.Equal(company.Id, contact.CompanyId);
    }

    [Fact]
    public async Task AFailedCompanyLeavesTheRowUnwritten()
    {
        var contact = Existing("CLI08000002", "Ana", "1010", IdentificationType.Cc);
        var version = contact.Version;
        var bed = new Bed(contact);

        var response = await bed.ImportAsync(Row(2, "Ana Maria", "1010", "999999999", cuc: "CLI08000002", type: "CC"));

        Assert.Equal("customers.import.row.company_not_found", ErrorOf(response, 2).Code);
        // Ni creada ni actualizada: el nombre y la versión siguen como estaban.
        Assert.Equal("Ana", contact.Name);
        Assert.Equal(version, contact.Version);
        Assert.Empty(bed.Repository.Added);
        Assert.Empty(response.Imported);
    }

    [Fact]
    public async Task ARowThatNamesARejectedCompanyRowIsRejectedToo()
    {
        // La empresa de la fila 2 falla (su Cuc no existe): quien la nombra no puede quedar vinculado a un cliente que no se escribió.
        var bed = new Bed();

        var response = await bed.ImportAsync(
            Row(2, "Verde SAS", "900111111", cuc: "CLI0800009Z"),
            Row(3, "Ana", "1010", "900111111", type: "CC"));

        Assert.Equal("customers.import.row.cuc_not_found", ErrorOf(response, 2).Code);
        Assert.Equal("customers.import.row.company_not_found", ErrorOf(response, 3).Code);
        Assert.Empty(bed.Repository.Added);
    }

    [Fact]
    public async Task ARowNamingAFileCompanyThatTheCompanyRulesRejectedIsRejectedToo()
    {
        var bed = new Bed();

        // X nombra una empresa que no existe y se rechaza: Z, que nombra a X, no puede quedar vinculada a una fila que no se escribe.
        var response = await bed.ImportAsync(
            Row(2, "X SAS", "900111111", "999999999"),
            Row(3, "Z", "2020", "900111111", type: "CC"));

        Assert.Equal("customers.import.row.company_not_found", ErrorOf(response, 2).Code);
        Assert.Equal("customers.import.row.company_not_found", ErrorOf(response, 3).Code);
        Assert.Empty(bed.Repository.Added);
    }

    [Fact]
    public async Task ANumberIsLookedUpOnceForTheWholeFile()
    {
        var company = Existing("CLI08000001", "Verde SAS", "900111111");
        var bed = new Bed(company);

        await bed.ImportAsync(
            Row(2, "Ana", "1010", "900111111", type: "CC"),
            Row(3, "Beto", "2020", "900111111", type: "CC"));

        Assert.Single(bed.Repository.IdentificationNumberLookups);
    }

    [Fact]
    public async Task AFileWithoutAnyCompanyCellNeitherLocksNorOpensATransaction()
    {
        var bed = new Bed();

        var response = await bed.ImportAsync(Row(2, "Ana", "1010", type: "CC"));

        Assert.Empty(response.Errors);
        Assert.Empty(bed.Repository.LockCalls);
        Assert.Equal(0, bed.Work.Begins);
        Assert.Equal(1, bed.Work.Saves);
    }
}
