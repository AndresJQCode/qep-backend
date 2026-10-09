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
