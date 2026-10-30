using Modules.Billing.Application;
using Modules.Billing.Domain;
using Modules.Tenancy.Application;
using static Modules.Billing.UnitTests.BillingImportTestDoubles;

namespace Modules.Billing.UnitTests;

/// <summary>
/// El emparejamiento de la importación (§3.2b, «el primero que aplica decide»): con documento decide sólo el documento; sin
/// documento, el teléfono; sin ninguno de los dos, el correo. Una clave que la fila trae y que no encuentra a nadie crea el
/// cliente: no se cae a la siguiente. Y el duplicado del archivo se mira sobre el cliente resuelto, no sobre la celda.
/// </summary>
public sealed class BillingImportAnalyzerTests
{
    private static readonly Guid Tenant = Guid.Parse("01900000-0000-7000-8000-000000000001");

    private const string Phone = "+573001234567";

    private static ExcelBillingRow Row(
        int rowNumber,
        string? type = "CC",
        string? number = "1020304050",
        string? phone = "300 123 4567",
        string? email = "pan@example.com",
        string service = "Hosting",
        string? channel = "WhatsApp") =>
        ValidRow(rowNumber) with
        {
            IdentificationType = type,
            IdentificationNumber = number,
            Phone = phone,
            Email = email,
            ServiceName = service,
            Channel = channel,
        };

    private static Task<BillingImportAnalysis> AnalyzeAsync(FakeCustomers customers, params ExcelBillingRow[] rows)
    {
        var phones = new FakePhones();
        var analyzer = new BillingImportAnalyzer(
            new FakeImporter(rows),
            new ExcelBillingRowRules(phones, new FakeIdentificationTypes(), new FixedClock()),
            phones,
            customers,
            new NoProfiles(),
            new NoServices(),
            new AnaManages(),
            new FixedTenantClock(),
            new CopCurrency(),
            new FixedClock());
        return analyzer.AnalyzeAsync(Tenant, "cobros.xlsx", 100, Stream.Null, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ANewDocumentWhosePhoneBelongsToAnExistingCustomerCreatesANewCustomer()
    {
        var customers = new FakeCustomers().With("Ferretería", phone: Phone);

        var analysis = await AnalyzeAsync(customers, Row(2));

        var planned = Assert.Single(analysis.Plan.Rows);
        Assert.Null(planned.MatchedCustomerId);
        Assert.Equal("create", planned.ToPreview().Customer.Action);
        Assert.Empty(analysis.Errors);
    }

    [Fact]
    public async Task APhoneThatMatchesNobodyCreatesTheCustomerEvenIfTheEmailBelongsToAnother()
    {
        var customers = new FakeCustomers().With("Otro", email: "pan@example.com");

        var analysis = await AnalyzeAsync(customers, Row(2, type: null, number: null));

        Assert.Null(Assert.Single(analysis.Plan.Rows).MatchedCustomerId);
    }

    [Fact]
    public async Task WithoutADocumentThePhoneDecides()
    {
        var customers = new FakeCustomers().With("Ferretería", phone: Phone);

        var analysis = await AnalyzeAsync(customers, Row(2, type: null, number: null));

        Assert.Equal(customers.Ids[0], Assert.Single(analysis.Plan.Rows).MatchedCustomerId);
    }

    [Fact]
    public async Task WithoutDocumentAndPhoneTheEmailDecides()
    {
        var customers = new FakeCustomers().With("Ferretería", phone: Phone, email: "pan@example.com");

        var analysis = await AnalyzeAsync(customers, Row(2, type: null, number: null, phone: null, channel: null));

        Assert.Equal(customers.Ids[0], Assert.Single(analysis.Plan.Rows).MatchedCustomerId);
    }

    [Fact]
    public async Task TwoCustomersWithThePhoneAreStillAmbiguous()
    {
        var customers = new FakeCustomers().With("Uno", phone: Phone).With("Dos", phone: Phone);

        var analysis = await AnalyzeAsync(customers, Row(2, type: null, number: null));

        Assert.Empty(analysis.Plan.Rows);
        Assert.Equal(BillingErrorCodes.ImportRowCustomerAmbiguous, Assert.Single(analysis.Errors).Code);
    }

    [Fact]
    public async Task TheSameMatchedCustomerReachedByDocumentAndByPhoneIsADuplicateService()
    {
        var customers = new FakeCustomers().With("Panadería", phone: Phone, document: ("CC", "1020304050"));

        var analysis = await AnalyzeAsync(customers, Row(2), Row(3, type: null, number: null));

        Assert.Equal([2], analysis.Plan.Rows.Select(row => row.Row.RowNumber));
        var error = Assert.Single(analysis.Errors);
        Assert.Equal((3, BillingErrorCodes.ImportRowDuplicateService), (error.RowNumber, error.Code));
    }

    [Fact]
    public async Task ARowWithoutADocumentJoinsTheNewCustomerOfAnEarlierRowWithItsPhone()
    {
        var analysis = await AnalyzeAsync(
            new FakeCustomers(), Row(2), Row(3, type: null, number: null, service: "Dominio"));

        Assert.Equal(2, analysis.Plan.Rows.Count);
        Assert.Single(analysis.Plan.Rows.Select(row => row.NewCustomerKey).Distinct());
        Assert.Equal(1, analysis.Plan.Summary.CustomersToCreate);
    }

    [Fact]
    public async Task ARowWithoutADocumentAndTheNewCustomerOfAnEarlierRowWithItsServiceIsADuplicate()
    {
        var analysis = await AnalyzeAsync(new FakeCustomers(), Row(2), Row(3, type: null, number: null));

        Assert.Equal([2], analysis.Plan.Rows.Select(row => row.Row.RowNumber));
        Assert.Equal((3, BillingErrorCodes.ImportRowDuplicateService), (analysis.Errors[0].RowNumber, analysis.Errors[0].Code));
    }

    [Fact]
    public async Task TwoNewDocumentsWithTheSamePhoneAreTwoCustomers()
    {
        var analysis = await AnalyzeAsync(new FakeCustomers(), Row(2), Row(3, number: "999"));

        Assert.Equal(2, analysis.Plan.Summary.CustomersToCreate);
    }

    [Fact]
    public async Task ARowWithoutADocumentWhosePhoneTwoNewCustomersShareIsAmbiguous()
    {
        var analysis = await AnalyzeAsync(
            new FakeCustomers(), Row(2), Row(3, number: "999"), Row(4, type: null, number: null, service: "Dominio"));

        Assert.Equal([2, 3], analysis.Plan.Rows.Select(row => row.Row.RowNumber));
        var error = Assert.Single(analysis.Errors);
        Assert.Equal((4, BillingErrorCodes.ImportRowCustomerAmbiguous), (error.RowNumber, error.Code));
    }

    private sealed class FakeImporter(IReadOnlyList<ExcelBillingRow> rows) : IExcelBillingImporter
    {
        public Task<IReadOnlyList<ExcelBillingRow>> ReadAsync(Stream content, CancellationToken cancellationToken) =>
            Task.FromResult(rows);
    }

    private sealed class FakeCustomers : IBillingCustomerLookup
    {
        private readonly List<(BillingCustomerRef Customer, (string Type, string Number)? Document)> customers = [];

        public Guid[] Ids =>customers.Select(entry => entry.Customer.Id).ToArray();

        public FakeCustomers With(string name, string? phone = null, string? email = null, (string, string)? document = null)
        {
            customers.Add((new BillingCustomerRef(Guid.NewGuid(), name, phone, email, true), document));
            return this;
        }

        public Task<IReadOnlyList<BillingCustomerRef>> FindByIdentificationAsync(
            Guid tenantId, string identificationType, string identificationNumber, CancellationToken cancellationToken) =>
            Matching(entry => entry.Document == (identificationType, identificationNumber));

        public Task<IReadOnlyList<BillingCustomerRef>> FindByPhoneE164Async(
            Guid tenantId, string phoneE164, CancellationToken cancellationToken) =>
            Matching(entry => entry.Customer.PhoneE164 == phoneE164);

        public Task<IReadOnlyList<BillingCustomerRef>> FindByEmailAsync(
            Guid tenantId, string email, CancellationToken cancellationToken) =>
            Matching(entry => string.Equals(entry.Customer.Email, email, StringComparison.OrdinalIgnoreCase));

        private Task<IReadOnlyList<BillingCustomerRef>> Matching(
            Func<(BillingCustomerRef Customer, (string Type, string Number)? Document), bool> predicate) =>
            Task.FromResult<IReadOnlyList<BillingCustomerRef>>(customers.Where(predicate).Select(entry => entry.Customer).ToArray());

        public Task<BillingCustomerRef?> FindAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<BillingCustomerRef>> FindManyAsync(
            Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyCollection<Guid>> SearchIdsAsync(Guid tenantId, string term, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class NoProfiles : ICustomerBillingProfileRepository
    {
        public Task<IReadOnlyList<CustomerBillingProfile>> ListByCustomersAsync(
            Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CustomerBillingProfile>>([]);

        public Task<CustomerBillingProfile?> FindAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CustomerBillingProfile>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void Add(CustomerBillingProfile profile) => throw new NotSupportedException();
    }

    private sealed class NoServices : IBillingServiceRepository
    {
        public Task<IReadOnlyList<BillingService>> ListOpenByCustomersAsync(
            Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<BillingService>>([]);

        public void Add(BillingService service) => throw new NotSupportedException();

        public Task<BillingService?> FindAsync(Guid tenantId, BillingServiceId id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<BillingService>> FindManyAsync(
            Guid tenantId, IReadOnlyCollection<BillingServiceId> ids, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<(IReadOnlyList<BillingService> Items, int Total)> ListAsync(
            BillingServiceQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<BillingService>> ListAllAsync(BillingServiceQuery query, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<BillingService>> ListActiveAsync(Guid tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class AnaManages : IBillingMemberDirectory
    {
        private static readonly BillingMemberRef Ana = new(Guid.NewGuid(), "Ana", "ana@example.com", true, true);

        public Task<BillingMemberRef?> FindManagerByEmailAsync(Guid tenantId, string email, CancellationToken cancellationToken) =>
            Task.FromResult<BillingMemberRef?>(email == Ana.Email ? Ana : null);

        public Task<IReadOnlyList<BillingMemberRef>> FindAsync(
            Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<BillingMemberRef>> ListManagersAsync(Guid tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FixedTenantClock : ITenantClock
    {
        public Task<TenantCalendar> GetAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult(new TenantCalendar(Now, TimeZoneInfo.Utc));
    }

    private sealed class CopCurrency : ITenantDefaultCurrency
    {
        public Task<string> GetAsync(Guid tenantId, CancellationToken cancellationToken) => Task.FromResult("COP");
    }
}
