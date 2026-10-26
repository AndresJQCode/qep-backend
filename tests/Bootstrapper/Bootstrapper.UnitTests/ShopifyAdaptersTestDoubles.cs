using System.Text;
using Modules.Catalog.Application;
using Modules.Catalog.Domain;
using Modules.Companies.Application;
using Modules.Companies.Domain;
using Modules.Customers.Application;
using Modules.Customers.Domain;
using Modules.Geography.Application;
using Modules.Geography.Domain;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper.UnitTests;

// Dobles de los repositorios y puertos que usan los adaptadores de Shopify. A mano y sin librería de mocking, como el
// resto del repositorio. Lo que ningún adaptador usa lanza, para que una prueba no pase por un camino que no creía estar
// ejerciendo.

internal sealed class FakeCarrierRepository(params Carrier[] carriers) : ICarrierRepository
{
    public Task<IReadOnlyList<Carrier>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Carrier>>(
            carriers.Where(carrier => carrier.TenantId == tenantId).OrderBy(carrier => carrier.SortOrder).ToList());

    public Task<Carrier?> FindAsync(Guid tenantId, CarrierId id, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public void Add(Carrier carrier) => throw new NotSupportedException();
}

internal sealed class FakeQuotationSettingsStore(Guid? freightTaxRateId) : IQuotationSettingsStore
{
    public Task<Guid?> GetFreightTaxRateIdAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult(freightTaxRateId);

    public Task<QuotationSettings> GetAsync(Guid tenantId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task SaveAsync(Guid tenantId, QuotationSettings settings, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task SetFreightTaxRateIdAsync(Guid tenantId, Guid? taxRateId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<FreightChargeModeSettings> GetFreightChargeModesAsync(Guid tenantId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task SetFreightChargeModesAsync(Guid tenantId, FreightChargeModeSettings settings, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

internal sealed class FakeTaxRateRepository(params TaxRate[] rates) : ITaxRateRepository
{
    public Task<TaxRate?> FindAsync(Guid tenantId, TaxRateId taxRateId, CancellationToken cancellationToken) =>
        Task.FromResult(rates.FirstOrDefault(rate => rate.TenantId == tenantId && rate.Id == taxRateId));

    public Task<IReadOnlyList<TaxRate>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<TaxRate?> FindByNameAsync(Guid tenantId, string name, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public void Add(TaxRate taxRate) => throw new NotSupportedException();

    public void Remove(TaxRate taxRate) => throw new NotSupportedException();
}

/// <summary>Anota cada llamada; <see cref="OnCreate"/>, <see cref="OnSync"/> y <see cref="OnCancel"/> deciden qué
/// responde o qué lanza.</summary>
internal sealed class FakeExternalOrderWriter : IExternalOrderWriter
{
    public Func<ExternalOrderDraft, ExternalOrderResult>? OnCreate { get; init; }

    public Action? OnSync { get; init; }

    public Action? OnCancel { get; init; }

    public List<ExternalOrderDraft> Created { get; } = [];

    public List<(Guid TenantId, Guid OrderId, OrderPaymentStatus Status)> Synced { get; } = [];

    public List<(Guid TenantId, Guid OrderId, Guid By, string Reason)> Cancelled { get; } = [];

    public ExternalOrderResult? LastResult { get; private set; }

    public int Calls { get; private set; }

    public Task<ExternalOrderResult> CreateAsync(ExternalOrderDraft draft, CancellationToken cancellationToken)
    {
        Calls++;
        Created.Add(draft);
        LastResult = OnCreate?.Invoke(draft)
            ?? new ExternalOrderResult(Guid.CreateVersion7(), "PED-1", 100m, 84m, draft.BillingAccount.Currency, draft.ClientId);
        return Task.FromResult(LastResult);
    }

    public Task SyncPaymentStatusAsync(Guid tenantId, Guid orderId, OrderPaymentStatus status, CancellationToken cancellationToken)
    {
        Calls++;
        OnSync?.Invoke();
        Synced.Add((tenantId, orderId, status));
        return Task.CompletedTask;
    }

    public Task CancelAsync(Guid tenantId, Guid orderId, Guid cancelledByMembershipId, string reason, CancellationToken cancellationToken)
    {
        Calls++;
        OnCancel?.Invoke();
        Cancelled.Add((tenantId, orderId, cancelledByMembershipId, reason));
        return Task.CompletedTask;
    }

    public Dictionary<(Guid TenantId, OrderSource Source, string Key), ExternalOrderResult> ByExternalKey { get; } = [];

    public Task<ExternalOrderResult?> FindByExternalKeyAsync(
        Guid tenantId, OrderSource source, string externalKey, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(ByExternalKey.GetValueOrDefault((tenantId, source, externalKey)));
    }

    public Func<ExternalShippingParty, ExternalShippingOutcome>? OnAttach { get; init; }

    public Func<ExternalShippingParty, ExternalShippingOutcome>? OnCorrect { get; init; }

    public List<(Guid TenantId, Guid OrderId, ExternalShippingParty Shipping, string NotesLinePrefix)> Attached { get; } = [];

    public List<(Guid TenantId, Guid OrderId, ExternalShippingParty Expected, Guid DepartmentId, Guid CityId)> Corrected { get; } = [];

    public Task<ExternalShippingOutcome> AttachShippingAsync(
        Guid tenantId, Guid orderId, ExternalShippingParty shipping, string notesLinePrefix, CancellationToken cancellationToken)
    {
        Calls++;
        Attached.Add((tenantId, orderId, shipping, notesLinePrefix));
        return Task.FromResult(
            OnAttach?.Invoke(shipping) ?? new ExternalShippingOutcome(ExternalShippingOutcomeKind.Applied, shipping, null));
    }

    public Task<ExternalShippingOutcome> CorrectShippingCityAsync(
        Guid tenantId, Guid orderId, ExternalShippingParty expected, Guid departmentId, Guid cityId, CancellationToken cancellationToken)
    {
        Calls++;
        Corrected.Add((tenantId, orderId, expected, departmentId, cityId));
        return Task.FromResult(
            OnCorrect?.Invoke(expected)
            ?? new ExternalShippingOutcome(
                ExternalShippingOutcomeKind.Applied, expected with { DepartmentId = departmentId, CityId = cityId }, null));
    }
}

internal sealed class PassThroughPhones : IPhoneNumberNormalizer
{
    public string? ToE164(string? phone, string country) => string.IsNullOrWhiteSpace(phone) ? null : phone.Trim();

    public bool IsKnownRegion(string regionCode) => true;

    public string? RegionOf(string? e164) => null;
}

/// <summary>Un normalizador de tabla: <c>(teléfono recortado, país)</c> → E.164; lo que no está no normaliza.</summary>
internal sealed class MappedPhones : Dictionary<(string Phone, string Country), string>, IPhoneNumberNormalizer
{
    public string? ToE164(string? phone, string country) =>
        phone is null ? null : TryGetValue((phone.Trim(), country), out var e164) ? e164 : null;

    public bool IsKnownRegion(string regionCode) => true;

    public string? RegionOf(string? e164) => null;
}

/// <summary>Los clientes que siembra la prueba. <see cref="Calls"/> cuenta toda lectura: lo que se prueba con él es
/// que el adaptador no fue a la base.</summary>
internal sealed class FakeCustomerRepository(params Customer[] customers) : ICustomerRepository
{
    public Dictionary<(IdentificationType Type, string Number), CustomerId> Identifications { get; } = [];

    public int Calls { get; private set; }

    public Task<Customer?> FindAsync(Guid tenantId, CustomerId customerId, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(customers.FirstOrDefault(customer => customer.TenantId == tenantId && customer.Id == customerId));
    }

    public Task<IReadOnlyDictionary<(IdentificationType Type, string Number), CustomerId>> FindExistingIdentificationsAsync(
        Guid tenantId, IReadOnlyCollection<(IdentificationType Type, string Number)> identifications, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult<IReadOnlyDictionary<(IdentificationType Type, string Number), CustomerId>>(
            Identifications.Where(pair => identifications.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value));
    }

    public Task<Customer?> FindOldestByPhoneE164Async(Guid tenantId, string phoneE164, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(customers.FirstOrDefault(customer => customer.TenantId == tenantId && customer.PhoneE164 == phoneE164));
    }

    public Task<CustomerId?> FindIdByEmailAsync(Guid tenantId, string email, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(customers
            .Where(customer => customer.TenantId == tenantId && string.Equals(customer.Email, email, StringComparison.OrdinalIgnoreCase))
            .Select(customer => (CustomerId?)customer.Id)
            .FirstOrDefault());
    }

    public Task<(IReadOnlyList<Customer> Items, int Total)> SearchAsync(
        Guid tenantId, string? search, string? name, string? identificationNumber, string? cuc, IReadOnlyCollection<Guid>? cityIds,
        bool? isComplete, int page, int pageSize, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlyList<Customer>> SearchSelectableAsync(
        Guid tenantId, string? search, int take, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlyList<Customer>> ListForExportAsync(
        Guid tenantId, string? search, string? name, string? identificationNumber, string? cuc, int skip, int take,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<bool> AnyWithClassificationAsync(
        Guid tenantId, ClientClassificationId classificationId, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlyDictionary<string, CustomerId>> FindIdsByCucSuffixAsync(
        Guid tenantId, IReadOnlyCollection<string> suffixes, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlySet<Guid>> SearchIdsByIdentificationNumberAsync(Guid tenantId, string term, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlySet<Guid>> SearchIdsByCucAsync(Guid tenantId, string term, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyDictionary<CustomerId, string>> FindNamesByIdsAsync(
        Guid tenantId, IReadOnlyCollection<CustomerId> customerIds, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlyDictionary<CustomerId, Customer>> FindManyAsync(
        Guid tenantId, IReadOnlyCollection<CustomerId> customerIds, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<Customer?> FindByWhatsAppUserIdAsync(Guid tenantId, string whatsAppUserId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<Customer?> FindByInstagramUserIdAsync(Guid tenantId, string instagramUserId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<bool> TryAttachWhatsAppUserIdAsync(Guid tenantId, CustomerId customerId, string whatsAppUserId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<bool> TryReplaceWhatsAppUserIdAsync(Guid tenantId, string previous, string current, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<CustomerWhatsAppRef>> FindWhatsAppRefsAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<Guid>> FindIdsByNameAsync(Guid tenantId, string term, int cap, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public void Add(Customer customer) => throw new NotSupportedException();
}

/// <summary>La fusión vista por el adaptador de Shopify: sólo el que quedó del fusionado dueño de un documento (desvío F9).</summary>
internal sealed class FakeCustomerMergeStore : ICustomerMergeStore
{
    public Dictionary<(IdentificationType Type, string Number), Guid> Survivors { get; } = [];

    public Task<Guid?> FindSurvivorIdByIdentificationAsync(
        Guid tenantId, IdentificationType type, string number, CancellationToken cancellationToken) =>
        Task.FromResult(Survivors.TryGetValue((type, number), out var survivor) ? survivor : (Guid?)null);

    public Task<IReadOnlyList<Guid>> FindIdsMergedIntoAsync(
        Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task RepointMergedIntoAsync(
        Guid tenantId, IReadOnlyCollection<Guid> customerIds, Guid survivorId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<CustomerMergeOwnedCounts> CountOwnedAsync(
        Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task MoveOwnedAsync(
        Guid tenantId, Guid survivorId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<Guid?> FindSurvivorIdAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

internal sealed class FakeCustomerProvisioning : ICustomerProvisioning
{
    public Action? OnComplete { get; set; }

    public List<(Guid TenantId, CompleteCustomerData Data)> Completes { get; } = [];

    public Guid LastId { get; private set; }

    public Task<ProvisionedCustomer> CreateCompleteAsync(Guid tenantId, CompleteCustomerData data, CancellationToken cancellationToken)
    {
        OnComplete?.Invoke();
        Completes.Add((tenantId, data));
        LastId = Guid.CreateVersion7();
        return Task.FromResult(new ProvisionedCustomer(LastId, true, true, true));
    }

    public Task<ProvisionedCustomer> CreateIncompleteAsync(
        Guid tenantId, string? name, string? phone, string? email, string? country, CancellationToken cancellationToken)
    {
        LastId = Guid.CreateVersion7();
        return Task.FromResult(new ProvisionedCustomer(LastId, false, true, true));
    }

    public Task<ProvisionedCustomer> CreateFromRegisterAsync(
        Guid tenantId, Guid actorId, RegisterCustomerData data, string? source, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<ExternalCustomerCityOutcome> CompleteExternalSaleAsync(
        Guid tenantId, Guid customerId, long expectedVersion, CompleteCustomerData data, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<ExternalCustomerCityOutcome> CorrectCityAsync(
        Guid tenantId, Guid customerId, long expectedVersion, Guid previousCityId, Guid cityId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

/// <summary>Una tienda Shopify enlazada en el estado que pide la prueba, con su ruta y el módulo prendido o no. El
/// <see cref="IIntegrationConnections"/> falso resuelve toda conexión Active sin mirar el módulo: así, que el módulo
/// apagado no deje operar lo tiene que decidir el adaptador.</summary>
internal sealed class ConnectionWorld
{
    public const string Domain = "mi-tienda.myshopify.com";
    public const string Token = "shpat_TEST_token";

    public ConnectionWorld(ConnectionStatus status, bool shopifyEnabled)
    {
        var now = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        Connection = IntegrationConnection.Create(
            IntegrationProviders.Shopify,
            Guid.CreateVersion7(),
            "Mi tienda",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ShopifyFieldKeys.ShopDomain] = Domain,
                [ShopifyFieldKeys.ShopName] = "Mi tienda",
                [ShopifyFieldKeys.ShopId] = "987654321",
            },
            new Dictionary<string, string>(StringComparer.Ordinal) { [ShopifyFieldKeys.AccessToken] = Token },
            (connectionId, fieldKey, plaintext) => new ProtectedSecret("k1", Encoding.UTF8.GetBytes(plaintext)),
            Guid.CreateVersion7(),
            now);
        if (status == ConnectionStatus.Paused)
        {
            Connection.Pause(now);
        }
        else if (status == ConnectionStatus.NeedsAttention)
        {
            Connection.MarkNeedsAttention("credentials_rejected", now);
        }

        var route = new ConnectionRoute(Connection.TenantId, Connection.Id, Connection.Status);
        Directory = new ShopifyConnectionDirectory(
            new FakeConnectionRoutes(new Dictionary<string, ConnectionRoute> { [Domain] = route }),
            new FakeConnectionRepository(Connection),
            new FakeIntegrationConnections(Connection),
            new FakeTenantModules(Connection.TenantId, TenantModuleSet.FromStored(shopifyEnabled
                ? TenantModuleKeys.DefaultForNewTenants
                : TenantModuleKeys.DefaultForNewTenants.Where(key => key != TenantModuleKeys.Shopify))));
    }

    public IntegrationConnection Connection { get; }

    public ShopifyConnectionDirectory Directory { get; }
}

internal sealed class FakeConnectionRoutes(Dictionary<string, ConnectionRoute> shopifyRoutes) : IConnectionRoutes
{
    public Task<ConnectionRoute?> FindAsync(string providerKey, string externalId, CancellationToken cancellationToken) =>
        Task.FromResult(providerKey == IntegrationProviders.Shopify.Key ? shopifyRoutes.GetValueOrDefault(externalId) : null);

    public Task<IReadOnlyList<ConnectionRoute>> FindByAccountAsync(string providerKey, string accountId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<ConnectionRoute>> ListByProviderAsync(string providerKey, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ConnectionRoute>>(
            providerKey == IntegrationProviders.Shopify.Key ? shopifyRoutes.Values.ToList() : []);
}

internal sealed class FakeConnectionRepository(params IntegrationConnection[] connections) : IIntegrationConnectionRepository
{
    public Task<IntegrationConnection?> FindAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        Task.FromResult(connections.FirstOrDefault(connection => connection.TenantId == tenantId && connection.Id == connectionId));

    public Task<IReadOnlyList<IntegrationConnection>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<IntegrationConnection>>([.. connections.Where(connection => connection.TenantId == tenantId)]);

    public Task<int> CountAsync(Guid tenantId, string providerKey, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public void Add(IntegrationConnection connection) => throw new NotSupportedException();

    public void Remove(IntegrationConnection connection) => throw new NotSupportedException();
}

internal sealed class FakeIntegrationConnections(params IntegrationConnection[] connections) : IIntegrationConnections
{
    public Task<ConnectionResolution> ResolveAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var connection = connections.FirstOrDefault(candidate =>
            candidate.TenantId == tenantId && candidate.Id == connectionId && candidate.Status == ConnectionStatus.Active);
        return Task.FromResult(connection is null
            ? ConnectionResolution.Unusable(ConnectionUnavailableReason.NotFound, null)
            : ConnectionResolution.Resolved(new ResolvedConnection(
                connection.Id, connection.ProviderKey, connection.Name,
                new Dictionary<string, string>(connection.Fields, StringComparer.Ordinal),
                connection.Secrets.ToDictionary(
                    secret => secret.FieldKey, secret => Encoding.UTF8.GetString(secret.Protected.Ciphertext), StringComparer.Ordinal))));
    }

    public Task<IReadOnlyList<ConnectionSummary>> ListActiveAsync(Guid tenantId, string providerKey, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<ConnectionListing>> ListByProviderAsync(Guid tenantId, string providerKey, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

internal sealed class FakeTenantModules(Guid tenantId, TenantModuleSet modules) : ITenantModules
{
    public Task<TenantModuleSet?> FindAsync(Guid tenantId1, CancellationToken cancellationToken) =>
        Task.FromResult(tenantId1 == tenantId ? modules : null);
}

internal sealed class RecordingHealthReporter : IConnectionHealthReporter
{
    public List<(Guid TenantId, Guid ConnectionId, string FailureCode)> Reports { get; } = [];

    public Task ReportCredentialsRejectedAsync(Guid tenantId, Guid connectionId, string failureCode, CancellationToken cancellationToken)
    {
        Reports.Add((tenantId, connectionId, failureCode));
        return Task.CompletedTask;
    }
}

internal sealed class NamedDepartments(params Department[] departments) : IDepartmentRepository
{
    public Task<Department?> FindByNameAsync(string name, CancellationToken cancellationToken) =>
        Task.FromResult(departments.FirstOrDefault(department => department.Name == name));

    public Task<IReadOnlyList<Department>> ListAllAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<Department?> FindAsync(DepartmentId departmentId, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlyList<Department>> ListByIdsAsync(
        IReadOnlyCollection<DepartmentId> departmentIds, CancellationToken cancellationToken) => throw new NotSupportedException();
}

internal sealed class NamedCities(params City[] cities) : ICityRepository
{
    public Task<City?> FindByNameAsync(DepartmentId departmentId, string name, CancellationToken cancellationToken) =>
        Task.FromResult(cities.FirstOrDefault(city => city.DepartmentId == departmentId && city.Name == name));

    public async Task<CityNameMatch?> MatchByNameAsync(DepartmentId departmentId, string name, CancellationToken cancellationToken) =>
        await FindByNameAsync(departmentId, name, cancellationToken) is { } city ? new CityNameMatch(city, SynonymId: null) : null;

    public Task<IReadOnlyList<City>> ListByDepartmentAsync(DepartmentId departmentId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<City?> FindAsync(CityId cityId, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlyList<City>> ListByIdsAsync(IReadOnlyCollection<CityId> cityIds, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<City>> ListByDepartmentsAsync(
        IReadOnlyCollection<DepartmentId> departmentIds, CancellationToken cancellationToken) => throw new NotSupportedException();
}

internal sealed class FakeCompanyRepository(params Company[] companies) : ICompanyRepository
{
    public Task<Company?> FindAsync(Guid tenantId, CompanyId companyId, CancellationToken cancellationToken) =>
        Task.FromResult(companies.FirstOrDefault(company => company.TenantId == tenantId && company.Id == companyId));

    public Task<IReadOnlyList<Company>> SearchAsync(
        Guid tenantId, string? search, string? name, string? taxId, CompanyStatusFilter? status, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public void Add(Company company) => throw new NotSupportedException();

    public void Remove(Company company) => throw new NotSupportedException();
}
