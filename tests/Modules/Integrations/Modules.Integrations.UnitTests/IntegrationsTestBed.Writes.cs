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
        new(Catalog, Repository, UnitOfWork, Audit, Protector, Tester, Modules, Providers, Memberships, AuthorNames,
            context ?? Context(), Clock, new CreateConnectionValidator(Catalog));

    public UpdateConnectionHandler UpdateHandler(IExecutionContext? context = null) =>
        new(Catalog, Repository, UnitOfWork, Audit, Protector, Tester, Modules, Providers, AuthorNames,
            context ?? Context(), Clock, new UpdateConnectionValidator());

    public FakeWhatsAppSignupGateway Gateway { get; } = new();

    public InMemoryRouteRepository Routes { get; } = new();

    public CompleteWhatsAppSignupHandler SignupHandler(IExecutionContext? context = null) =>
        new(Catalog, Repository, Routes, UnitOfWork, Audit, Protector, Gateway, MetaApp, Modules, Memberships, AuthorNames,
            context ?? Context(), Clock, new CompleteWhatsAppSignupValidator());

    public FakeInstagramAppSettings InstagramApp { get; } = new();

    public ConnectInstagramHandler InstagramHandler(IExecutionContext? context = null) =>
        new(Catalog, Repository, Routes, UnitOfWork, Audit, Protector, Gateway.Instagram, InstagramApp, Modules, Memberships, AuthorNames,
            context ?? Context(), Clock, new ConnectInstagramValidator());
}

/// <summary>El caso de uso Instagram (spec 2026-10-14): configurado por defecto, con el origen de producción y el de local
/// permitidos.</summary>
internal sealed class FakeInstagramAppSettings : IInstagramAppSettings
{
    public bool Configured { get; set; } = true;

    public List<string> AllowedOrigins { get; } = ["https://qep.qcode.co", "http://localhost:3002"];

    public bool IsConfigured => Configured;

    public string? AppId => Configured ? "700800900" : null;

    public string GraphApiVersion => "v24.0";

    public bool IsAllowedRedirect(Uri redirectUri) =>
        AllowedOrigins.Contains(redirectUri.GetLeftPart(UriPartial.Authority), StringComparer.OrdinalIgnoreCase);
}

/// <summary>Business Login de mentira: responde por paso y anota el orden. <see cref="FailAt"/> hace fallar un paso con un
/// 400 y el código de <see cref="FailureCode"/>.</summary>
internal sealed class FakeInstagramLoginGateway : IInstagramLoginGateway
{
    public const string IgUserId = "17841400000000001";
    public const string ShortLivedToken = "ig-short-token-SENTINEL-unit";
    public const string LongLivedToken = "ig-long-token-SENTINEL-unit";
    public const string RefreshedToken = "ig-refreshed-token-SENTINEL-unit";
    public const long ExpiresInSeconds = 5_184_000;

    public List<string> Calls { get; } = [];

    public string? FailAt { get; set; }

    public int FailureCode { get; set; } = 100;

    public int FailureStatus { get; set; } = 400;

    public string? LastRedirectUri { get; private set; }

    public string? LastRefreshedToken { get; private set; }

    public List<string> Permissions { get; set; } = [InstagramLogin.ScopeBasic, InstagramLogin.ScopeManageMessages];

    public string? AccountType { get; set; } = "BUSINESS";

    private GraphFailure Failure => new(FailureStatus, FailureCode, null, $"http_{FailureStatus}");

    public Task<GraphResult<InstagramShortLivedToken>> ExchangeCodeAsync(string code, string redirectUri, CancellationToken cancellationToken)
    {
        Calls.Add("exchange");
        LastRedirectUri = redirectUri;
        return Task.FromResult(FailAt == "exchange"
            ? new GraphResult<InstagramShortLivedToken>(null, Failure)
            : new GraphResult<InstagramShortLivedToken>(new InstagramShortLivedToken(ShortLivedToken, IgUserId, Permissions), null));
    }

    public Task<GraphResult<InstagramLongLivedToken>> ExchangeLongLivedAsync(string shortLivedToken, CancellationToken cancellationToken)
    {
        Calls.Add("long-lived");
        return Task.FromResult(FailAt == "long-lived"
            ? new GraphResult<InstagramLongLivedToken>(null, Failure)
            : new GraphResult<InstagramLongLivedToken>(new InstagramLongLivedToken(LongLivedToken, TimeSpan.FromSeconds(ExpiresInSeconds)), null));
    }

    public Task<GraphResult<InstagramLongLivedToken>> RefreshAsync(string longLivedToken, CancellationToken cancellationToken)
    {
        Calls.Add("refresh");
        LastRefreshedToken = longLivedToken;
        return Task.FromResult(FailAt == "refresh"
            ? new GraphResult<InstagramLongLivedToken>(null, Failure)
            : new GraphResult<InstagramLongLivedToken>(new InstagramLongLivedToken(RefreshedToken, TimeSpan.FromSeconds(ExpiresInSeconds)), null));
    }

    public Task<GraphResult<InstagramAccount>> GetMeAsync(string accessToken, CancellationToken cancellationToken)
    {
        Calls.Add("me");
        return Task.FromResult(FailAt == "me"
            ? new GraphResult<InstagramAccount>(null, Failure)
            : new GraphResult<InstagramAccount>(new InstagramAccount(IgUserId, "origenbotanico", "Origen Botánico", AccountType), null));
    }

    public Task<GraphResult<bool>> SubscribeAsync(string igUserId, string accessToken, CancellationToken cancellationToken)
    {
        Calls.Add($"subscribe:{igUserId}");
        return Task.FromResult(FailAt == "subscribe" ? new GraphResult<bool>(false, Failure) : new GraphResult<bool>(true, null));
    }
}

/// <summary>Graph de mentira para el signup: responde por paso y anota el orden. <see cref="FailAt"/> hace
/// fallar un paso con un error 400 y el código de <see cref="FailureCode"/> (100 si no se fija).</summary>
internal sealed class FakeWhatsAppSignupGateway : IWhatsAppSignupGateway
{
    public List<string> Calls { get; } = [];

    /// <summary>El de Instagram, colgado acá para que el test bed exponga una sola puerta a Meta.</summary>
    public FakeInstagramLoginGateway Instagram { get; } = new();

    public string? FailAt { get; set; }

    public int FailureCode { get; set; } = 100;

    public string? LastPin { get; private set; }

    public Dictionary<string, WabaPhoneNumber> PhoneNumbers { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, IReadOnlyList<WabaPhoneNumber>> WabaNumbers { get; } = new(StringComparer.Ordinal);

    private GraphFailure Failure => new(400, FailureCode, null, "http_400");

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

    /// <summary>Hace fallar sólo la primera lectura del número: la que el signup hace antes de decidir si registra.</summary>
    public bool FailFirstRead { get; set; }

    public Task<GraphResult<WabaPhoneNumber>> GetPhoneNumberAsync(string phoneNumberId, string accessToken, CancellationToken cancellationToken)
    {
        Calls.Add($"read:{phoneNumberId}");
        var failThisRead = FailFirstRead && Calls.Count(call => call.StartsWith("read:", StringComparison.Ordinal)) == 1;
        return Task.FromResult(FailAt == "read" || failThisRead || !PhoneNumbers.TryGetValue(phoneNumberId, out var number)
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
