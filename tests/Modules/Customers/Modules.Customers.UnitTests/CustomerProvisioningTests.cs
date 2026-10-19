using BuildingBlocks.Application;
using FluentValidation;
using Modules.Customers.Application;
using Modules.Customers.Domain;

namespace Modules.Customers.UnitTests;

/// <summary>Spec de Shopify 2026-10-15 §6.2.1 pasos 3 y 4 sin base: el alta de un comprador desde una integración
/// repite el orden de <c>CreateCustomerHandler</c> (clasificación y ciudad antes del CUC), gana la carrera del documento
/// releyendo y audita con actor <c>Integration</c>.</summary>
public sealed class CustomerProvisioningTests
{
    private static readonly Guid Tenant = Guid.Parse("01900000-0000-7000-8000-0000000000c1");
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid CityId = Guid.Parse("01900000-0000-7000-8000-0000000000c2");
    private static readonly ClientClassification Classification =
        ClientClassification.Create(ClientClassificationId.New(), Tenant, "Mediano", "CLI", Now);

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class PlusNormalizer : IPhoneNumberNormalizer
    {
        public string? ToE164(string? phone, string country) =>
            phone is null ? null : "+57" + new string(phone.Where(char.IsDigit).ToArray());

        public bool IsKnownRegion(string regionCode) => regionCode == "CO";

        public string? RegionOf(string? e164) => null;
    }

    private sealed class RecordingAudit : ICustomersAuditPublisher
    {
        public List<string> Human { get; } = [];

        public List<(Guid Tenant, string Action, string ResourceId, string Outcome)> ByIntegration { get; } = [];

        public void Publish(Guid tenantId, Guid actorId, string action, string resourceId, string outcome, DateTimeOffset occurredAt) =>
            Human.Add(action);

        public void PublishByIntegration(Guid tenantId, string action, string resourceId, string outcome, DateTimeOffset occurredAt) =>
            ByIntegration.Add((tenantId, action, resourceId, outcome));
    }

    private sealed class UnitOfWork(Exception? failure = null) : ICustomersUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken) =>
            failure is null ? Task.FromResult(1) : Task.FromException<int>(failure);
    }

    private sealed class Classifications(List<string> calls) : IClientClassificationRepository
    {
        public Task<ClientClassification?> FindAsync(Guid tenantId, ClientClassificationId classificationId, CancellationToken cancellationToken)
        {
            calls.Add("classification");
            return Task.FromResult<ClientClassification?>(
                tenantId == Tenant && classificationId == Classification.Id ? Classification : null);
        }

        public Task<IReadOnlyList<ClientClassification>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ClientClassification>> ListByIdsAsync(Guid tenantId, IReadOnlyCollection<ClientClassificationId> classificationIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ClientClassification?> FindByNameAsync(Guid tenantId, string name, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void Add(ClientClassification classification) => throw new NotSupportedException();

        public void Remove(ClientClassification classification) => throw new NotSupportedException();
    }

    private sealed class Geography(List<string> calls) : ICustomerGeographyLookup
    {
        public Task<CustomerCityRef?> FindCityAsync(Guid cityId, CancellationToken cancellationToken)
        {
            calls.Add("city");
            return Task.FromResult<CustomerCityRef?>(
                cityId == CityId ? new CustomerCityRef(CityId, "08001", "Barranquilla", Guid.NewGuid(), "08", "Atlántico") : null);
        }

        public Task<IReadOnlyDictionary<Guid, CustomerCityRef>> FindCitiesAsync(IReadOnlyCollection<Guid> cityIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CustomerCityRef?> FindCityByNameAsync(string departmentName, string cityName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CustomerDepartmentDto>> ListDepartmentsAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CustomerDepartmentWithCitiesDto>> ListDepartmentsWithCitiesAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyCollection<Guid>> ListCityIdsByDepartmentsAsync(IReadOnlyCollection<Guid> departmentIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class Cucs(List<string> calls) : ICucGenerator
    {
        public Task<long> NextAsync(Guid tenantId, CancellationToken cancellationToken)
        {
            calls.Add("cuc");
            return Task.FromResult(7L);
        }

        public Task<long> NextBatchAsync(Guid tenantId, int count, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class Customers : ICustomerRepository
    {
        public List<Customer> Added { get; } = [];

        public Customer? Existing { get; init; }

        public Task<IReadOnlyDictionary<(IdentificationType Type, string Number), CustomerId>> FindExistingIdentificationsAsync(
            Guid tenantId, IReadOnlyCollection<(IdentificationType Type, string Number)> identifications, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<(IdentificationType Type, string Number), CustomerId>>(
                Existing is { IdentificationType: { } type, IdentificationNumber: { } number } && tenantId == Existing.TenantId &&
                identifications.Contains((type, number))
                    ? new Dictionary<(IdentificationType, string), CustomerId> { [(type, number)] = Existing.Id }
                    : new Dictionary<(IdentificationType, string), CustomerId>());

        public Task<Customer?> FindAsync(Guid tenantId, CustomerId customerId, CancellationToken cancellationToken) =>
            Task.FromResult(Existing is not null && Existing.TenantId == tenantId && Existing.Id == customerId ? Existing : null);

        public void Add(Customer customer) => Added.Add(customer);

        public Task<CustomerId?> FindIdByEmailAsync(Guid tenantId, string email, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<(IReadOnlyList<Customer> Items, int Total)> SearchAsync(Guid tenantId, string? search, string? name, string? identificationNumber, string? cuc, IReadOnlyCollection<Guid>? cityIds, bool? isComplete, int page, int pageSize, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Customer>> ListForExportAsync(Guid tenantId, string? search, string? name, string? identificationNumber, string? cuc, int skip, int take, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> AnyWithClassificationAsync(Guid tenantId, ClientClassificationId classificationId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, CustomerId>> FindIdsByCucSuffixAsync(Guid tenantId, IReadOnlyCollection<string> suffixes, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlySet<Guid>> SearchIdsByIdentificationNumberAsync(Guid tenantId, string term, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlySet<Guid>> SearchIdsByCucAsync(Guid tenantId, string term, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<CustomerId, string>> FindNamesByIdsAsync(Guid tenantId, IReadOnlyCollection<CustomerId> customerIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<CustomerId, Customer>> FindManyAsync(Guid tenantId, IReadOnlyCollection<CustomerId> customerIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Customer?> FindByWhatsAppUserIdAsync(Guid tenantId, string whatsAppUserId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Customer?> FindByInstagramUserIdAsync(Guid tenantId, string instagramUserId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Customer?> FindOldestByPhoneE164Async(Guid tenantId, string phoneE164, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> TryAttachWhatsAppUserIdAsync(Guid tenantId, CustomerId customerId, string whatsAppUserId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> TryReplaceWhatsAppUserIdAsync(Guid tenantId, string previous, string current, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CustomerWhatsAppRef>> FindWhatsAppRefsAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Guid>> FindIdsByNameAsync(Guid tenantId, string term, int cap, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed record Bed(
        CustomerProvisioning Provisioning, Customers Customers, RecordingAudit Audit, List<string> Calls);

    private static Bed NewBed(Customer? existing = null, Exception? saveFailure = null)
    {
        var calls = new List<string>();
        var customers = new Customers { Existing = existing };
        var audit = new RecordingAudit();
        var provisioning = new CustomerProvisioning(
            customers,
            new Classifications(calls),
            new Geography(calls),
            new UnitOfWork(saveFailure),
            audit,
            new Cucs(calls),
            new FixedClock(),
            new CreateCustomerValidator(),
            new PlusNormalizer());
        return new Bed(provisioning, customers, audit, calls);
    }

    private static CompleteCustomerData Buyer(string? email = "Laura@Verde.co") =>
        new("Laura Pérez", "CC", "1020304050", "310 935 2187", email, "Calle 10 # 45-12", "CO", CityId, null, Classification.Id.Value);

    [Fact]
    public async Task CreateCompleteIssuesCucAfterClassificationAndCity()
    {
        var bed = NewBed();

        var result = await bed.Provisioning.CreateCompleteAsync(Tenant, Buyer(), TestContext.Current.CancellationToken);

        Assert.Equal(["classification", "city", "cuc"], bed.Calls);
        var customer = Assert.Single(bed.Customers.Added);
        Assert.Equal(new ProvisionedCustomer(customer.Id.Value, true, true, true), result);
        Assert.Equal("CLI08000007", customer.Cuc);
        Assert.Equal(Tenant, customer.TenantId);
        Assert.False(customer.WithRetention);
        Assert.False(customer.VatSurplus);
        Assert.Equal("+573109352187", customer.PhoneE164);
        // La libreta nace con la dirección del alta, como en el POST.
        Assert.Equal(CityId, Assert.Single(customer.Addresses).CityId);
    }

    [Fact]
    public async Task CreateCompleteInvalidDataFailsBeforeIssuingCuc()
    {
        var bed = NewBed();

        await Assert.ThrowsAsync<ValidationException>(() =>
            bed.Provisioning.CreateCompleteAsync(Tenant, Buyer(email: "no-es-un-correo"), TestContext.Current.CancellationToken));

        Assert.Empty(bed.Calls);
        Assert.Empty(bed.Customers.Added);
    }

    [Fact]
    public async Task CreateCompleteIdentificationTakenRaceReturnsExisting()
    {
        var existing = Customer.Create(
            CustomerId.New(), Tenant, "CLI08000001", "Laura", null, null,
            new CustomerIdentification { Type = IdentificationType.Cc, Number = "1020304050" },
            new CustomerContactInfo { Phone = "3000000000", Email = "otra@verde.co", Address = "Calle 1", Country = "CO", CityId = CityId },
            new CustomerCommercialInfo { ClassificationId = Classification.Id },
            Now);
        var bed = NewBed(existing, new CustomersDomainException("customers.customer.identification_taken", "taken"));

        var result = await bed.Provisioning.CreateCompleteAsync(Tenant, Buyer(), TestContext.Current.CancellationToken);

        Assert.Equal(new ProvisionedCustomer(existing.Id.Value, true, true, false), result);
    }

    [Fact]
    public async Task CreateCompleteOtherDomainFailureIsNotSwallowed()
    {
        var bed = NewBed(saveFailure: new CustomersDomainException("customers.customer.cuc_taken", "taken"));

        var failure = await Assert.ThrowsAsync<CustomersDomainException>(() =>
            bed.Provisioning.CreateCompleteAsync(Tenant, Buyer(), TestContext.Current.CancellationToken));

        Assert.Equal("customers.customer.cuc_taken", failure.Code);
    }

    [Fact]
    public async Task CreateCompleteAuditsWithIntegrationActor()
    {
        var bed = NewBed();

        var result = await bed.Provisioning.CreateCompleteAsync(Tenant, Buyer(), TestContext.Current.CancellationToken);

        Assert.Empty(bed.Audit.Human);
        Assert.Equal((Tenant, "customers.customer.created", result.Id.ToString(), "success"), Assert.Single(bed.Audit.ByIntegration));
    }

    [Fact]
    public async Task CreateIncompleteWithoutNameUsesShopifyDefault()
    {
        var bed = NewBed();

        var result = await bed.Provisioning.CreateIncompleteAsync(
            Tenant, "  ", "310 935 2187", "laura@verde.co", "CO", TestContext.Current.CancellationToken);

        var customer = Assert.Single(bed.Customers.Added);
        Assert.Equal(new ProvisionedCustomer(customer.Id.Value, false, true, true), result);
        Assert.Equal(CustomerProvisioning.ExternalSaleFallbackName, customer.Name);
        Assert.Equal("Cliente de Shopify", CustomerProvisioning.ExternalSaleFallbackName);
        Assert.Equal("+573109352187", customer.PhoneE164);
        Assert.Empty(bed.Calls);
        Assert.Equal((Tenant, "customers.customer.created", result.Id.ToString(), "success"), Assert.Single(bed.Audit.ByIntegration));
    }

    [Fact]
    public async Task CreateIncompleteTrimsLongNameToTheColumn()
    {
        var bed = NewBed();

        await bed.Provisioning.CreateIncompleteAsync(
            Tenant, new string('a', Customer.NameMaxLength + 10), null, null, null, TestContext.Current.CancellationToken);

        Assert.Equal(Customer.NameMaxLength, Assert.Single(bed.Customers.Added).Name.Length);
    }
}
