using Modules.Audit.Domain;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Integrations.UnitTests;

internal sealed partial class IntegrationsTestBed
{
    public FakeConnectionTester Tester { get; } = new();

    public RecordingAuditRecorder Audit { get; } = new();

    public CreateConnectionHandler CreateHandler(IExecutionContext? context = null) =>
        new(Catalog, Repository, UnitOfWork, Audit, Protector, Tester, Modules, Memberships, AuthorNames,
            context ?? Context(), Clock, new CreateConnectionValidator(Catalog));

    public UpdateConnectionHandler UpdateHandler(IExecutionContext? context = null) =>
        new(Catalog, Repository, UnitOfWork, Audit, Protector, Tester, Modules, AuthorNames,
            context ?? Context(), Clock, new UpdateConnectionValidator());

    public FakeWhatsAppSignupGateway Gateway { get; } = new();

    public InMemoryRouteRepository Routes { get; } = new();

    public CompleteWhatsAppSignupHandler SignupHandler(IExecutionContext? context = null) =>
        new(Catalog, Repository, Routes, UnitOfWork, Audit, Protector, Gateway, MetaApp, Modules, Memberships, AuthorNames,
            context ?? Context(), Clock, new CompleteWhatsAppSignupValidator());
}

/// <summary>Graph de mentira para el signup: responde por paso y anota el orden. <see cref="FailAt"/> hace
/// fallar un paso con un error 400 código 100.</summary>
internal sealed class FakeWhatsAppSignupGateway : IWhatsAppSignupGateway
{
    public List<string> Calls { get; } = [];

    public string? FailAt { get; set; }

    public string? LastPin { get; private set; }

    public Dictionary<string, WabaPhoneNumber> PhoneNumbers { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, IReadOnlyList<WabaPhoneNumber>> WabaNumbers { get; } = new(StringComparer.Ordinal);

    private static GraphFailure Failure => new(400, 100, null, "http_400");

    public Task<GraphResult<string>> ExchangeCodeAsync(string code, CancellationToken cancellationToken)
    {
        Calls.Add("exchange");
        return Task.FromResult(FailAt == "exchange" ? new GraphResult<string>(null, Failure) : new GraphResult<string>("meta-access-token-SENTINEL-unit", null));
    }

    public Task<GraphResult<bool>> RegisterNumberAsync(string phoneNumberId, string accessToken, string pin, CancellationToken cancellationToken)
    {
        Calls.Add($"register:{phoneNumberId}");
        LastPin = pin;
        return Task.FromResult(FailAt == "register" ? new GraphResult<bool>(false, Failure) : new GraphResult<bool>(true, null));
    }

    public Task<GraphResult<IReadOnlyList<WabaPhoneNumber>>> ListPhoneNumbersAsync(string wabaId, string accessToken, CancellationToken cancellationToken)
    {
        Calls.Add($"numbers:{wabaId}");
        return Task.FromResult(FailAt == "numbers"
            ? new GraphResult<IReadOnlyList<WabaPhoneNumber>>(null, Failure)
            : new GraphResult<IReadOnlyList<WabaPhoneNumber>>(WabaNumbers.GetValueOrDefault(wabaId) ?? [], null));
    }

    public Task<GraphResult<bool>> SubscribeAppAsync(string wabaId, string accessToken, CancellationToken cancellationToken)
    {
        Calls.Add($"subscribe:{wabaId}");
        return Task.FromResult(FailAt == "subscribe" ? new GraphResult<bool>(false, Failure) : new GraphResult<bool>(true, null));
    }

    public Task<GraphResult<WabaPhoneNumber>> GetPhoneNumberAsync(string phoneNumberId, string accessToken, CancellationToken cancellationToken)
    {
        Calls.Add($"read:{phoneNumberId}");
        return Task.FromResult(FailAt == "read" || !PhoneNumbers.TryGetValue(phoneNumberId, out var number)
            ? new GraphResult<WabaPhoneNumber>(null, Failure)
            : new GraphResult<WabaPhoneNumber>(number, null));
    }
}

internal sealed class InMemoryRouteRepository : IConnectionRouteRepository
{
    public List<IntegrationConnectionRoute> Added { get; } = [];

    public HashSet<(string ProviderKey, string ExternalId)> Existing { get; } = [];

    public void Add(IntegrationConnectionRoute route) => Added.Add(route);

    public Task<bool> ExistsAsync(string providerKey, string externalId, CancellationToken cancellationToken) =>
        Task.FromResult(Existing.Contains((providerKey, externalId)) || Added.Any(route => route.ProviderKey == providerKey && route.ExternalId == externalId));
}

internal sealed record TesterCall(
    string ProviderKey, IReadOnlyDictionary<string, string> Fields, IReadOnlyDictionary<string, string> Secrets);

/// <summary>El proveedor de mentira: anota lo que se probó y responde <see cref="Result"/>.</summary>
internal sealed class FakeConnectionTester : IConnectionTester
{
    public ConnectionTestResult Result { get; set; } = ConnectionTestResult.Ok;

    public List<TesterCall> Calls { get; } = [];

    public Task<ConnectionTestResult> TestAsync(
        IntegrationProvider provider,
        IReadOnlyDictionary<string, string> fields,
        IReadOnlyDictionary<string, string> secrets,
        CancellationToken cancellationToken)
    {
        Calls.Add(new TesterCall(
            provider.Key, new Dictionary<string, string>(fields), new Dictionary<string, string>(secrets)));
        return Task.FromResult(Result);
    }
}

internal sealed record AuditRecord(
    Guid TenantId,
    Guid ActorId,
    AuditActorType ActorType,
    string Action,
    Guid ConnectionId,
    string Outcome,
    IReadOnlyCollection<string> ChangedFields,
    DateTimeOffset OccurredAt);

internal sealed class RecordingAuditRecorder : IIntegrationsAuditRecorder
{
    public List<AuditRecord> Entries { get; } = [];

    public void Record(
        Guid tenantId,
        Guid actorId,
        AuditActorType actorType,
        string action,
        Guid connectionId,
        string outcome,
        IReadOnlyCollection<string> changedFields,
        DateTimeOffset occurredAt) =>
        Entries.Add(new AuditRecord(tenantId, actorId, actorType, action, connectionId, outcome, changedFields, occurredAt));
}

/// <summary>Criterio 5 del spec: un catálogo con un proveedor que sólo existe en las pruebas.</summary>
internal sealed class FakeIntegrationProviderCatalog(params IntegrationProvider[] providers) : IIntegrationProviderCatalog
{
    public IReadOnlyList<IntegrationProvider> All => providers;

    public IntegrationProvider? Find(string? key) =>
        providers.FirstOrDefault(provider => string.Equals(provider.Key, key, StringComparison.Ordinal));
}
