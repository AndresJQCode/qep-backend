using BuildingBlocks.Application;
using Modules.Customers.Application;
using Modules.Customers.Domain;

namespace Modules.Customers.UnitTests;

/// <summary>Dobles de los casos de uso de escritura de clientes (alta, edición, aprovisionamiento), compartidos por
/// <c>CustomerProvisioningTests</c> y <c>CustomerWriteHandlersTests</c>. Viven anidados en una clase estática, y no como
/// tipos sueltos del namespace, para no tapar a los homónimos de <c>EngagementTestBed</c> (su <c>FixedClock</c> tiene otra
/// fecha) en los archivos que lo importan con <c>using static</c>.</summary>
internal static class CustomerWriteDoubles
{
    public static readonly Guid Tenant = Guid.Parse("01900000-0000-7000-8000-0000000000c1");
    public static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
    public static readonly Guid CityId = Guid.Parse("01900000-0000-7000-8000-0000000000c2");
    public static readonly ClientClassification Classification =
        ClientClassification.Create(ClientClassificationId.New(), Tenant, "Mediano", "CLI", Now);

    public sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    public sealed class PlusNormalizer : IPhoneNumberNormalizer
    {
        public string? ToE164(string? phone, string country) =>
            phone is null ? null : "+57" + new string(phone.Where(char.IsDigit).ToArray());

        public bool IsKnownRegion(string regionCode) => regionCode == "CO";

        public string? RegionOf(string? e164) => null;
    }

    public sealed class RecordingAudit : ICustomersAuditPublisher
    {
        public List<(Guid Tenant, Guid Actor, string Action, string ResourceId, string Outcome, string? Source)> Human { get; } = [];

        public List<(Guid Tenant, string Action, string ResourceId, string Outcome)> ByIntegration { get; } = [];

        public void Publish(Guid tenantId, Guid actorId, string action, string resourceId, string outcome, DateTimeOffset occurredAt, string? source = null) =>
            Human.Add((tenantId, actorId, action, resourceId, outcome, source));

        public void PublishByIntegration(Guid tenantId, string action, string resourceId, string outcome, DateTimeOffset occurredAt) =>
            ByIntegration.Add((tenantId, action, resourceId, outcome));
    }

    public sealed class UnitOfWork(Exception? failure = null) : ICustomersUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken) =>
            failure is null ? Task.FromResult(1) : Task.FromException<int>(failure);

        public Task<ICustomersTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
            Task.FromResult<ICustomersTransaction>(NoTransaction.Instance);
    }

    /// <summary>La transacción que no hace nada: estas pruebas no ejercen la atomicidad, que prueba
    /// <c>CustomerMergeStoreTests</c> contra la base.</summary>
    public sealed class NoTransaction : ICustomersTransaction
    {
        public static readonly NoTransaction Instance = new();

        public Task CommitAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public sealed class Classifications(List<string> calls) : IClientClassificationRepository
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

    public sealed class Geography(List<string> calls) : ICustomerGeographyLookup
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

    public sealed class Cucs(List<string> calls) : ICucGenerator
    {
        public Task<long> NextAsync(Guid tenantId, CancellationToken cancellationToken)
        {
            calls.Add("cuc");
            return Task.FromResult(7L);
        }

        public Task<long> NextBatchAsync(Guid tenantId, int count, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    public sealed class FakeCustomerRepository : ICustomerRepository
    {
        public List<Customer> Added { get; } = [];

        public Customer? Existing { get; init; }

        /// <summary>Otros clientes que <see cref="FindAsync"/> encuentra (el que un PUT edita, distinto del dueño del documento).</summary>
        public List<Customer> Known { get; } = [];

        /// <summary>Cuántas veces se pidió el dueño de un documento: un guardado que sale bien no debería pedirlo nunca.</summary>
        public int IdentificationLookups { get; private set; }

        public Task<IReadOnlyDictionary<(IdentificationType Type, string Number), CustomerId>> FindExistingIdentificationsAsync(
            Guid tenantId, IReadOnlyCollection<(IdentificationType Type, string Number)> identifications, CancellationToken cancellationToken)
        {
            IdentificationLookups++;
            return Task.FromResult<IReadOnlyDictionary<(IdentificationType Type, string Number), CustomerId>>(
                Existing is { IdentificationType: { } type, IdentificationNumber: { } number } && tenantId == Existing.TenantId &&
                identifications.Contains((type, number))
                    ? new Dictionary<(IdentificationType, string), CustomerId> { [(type, number)] = Existing.Id }
                    : new Dictionary<(IdentificationType, string), CustomerId>());
        }

        public Task<Customer?> FindAsync(Guid tenantId, CustomerId customerId, CancellationToken cancellationToken) =>
            Task.FromResult(
                Existing is not null && Existing.TenantId == tenantId && Existing.Id == customerId
                    ? Existing
                    : Known.SingleOrDefault(item => item.TenantId == tenantId && item.Id == customerId));

        public void Add(Customer customer) => Added.Add(customer);

        public Task<CustomerId?> FindIdByEmailAsync(Guid tenantId, string email, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<(IReadOnlyList<Customer> Items, int Total)> SearchAsync(Guid tenantId, string? search, string? name, string? identificationNumber, string? cuc, IReadOnlyCollection<Guid>? cityIds, bool? isComplete, int page, int pageSize, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Customer>> SearchSelectableAsync(Guid tenantId, string? search, int take, CancellationToken cancellationToken) =>
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
}
