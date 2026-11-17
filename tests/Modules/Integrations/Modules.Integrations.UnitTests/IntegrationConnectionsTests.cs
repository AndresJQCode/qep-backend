using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Tenancy.Domain;

namespace Modules.Integrations.UnitTests;

/// <summary>
/// Consultas por request, fase 2: las listas del puerto leen las conexiones del tenant una sola vez. La base está en otro nodo
/// y cada lectura en serie cuesta su viaje; resolver una por una era una lectura por conexión más las de módulos y proveedores.
/// </summary>
public sealed class IntegrationConnectionsTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeSecretProtector _protector = new();

    [Fact]
    public async Task ListResolvedActiveReadsTheRepositoryOnceAndSkipsWhatResolveWouldRefuse()
    {
        var active = Connection(status: ConnectionStatus.Active);
        var paused = Connection(status: ConnectionStatus.Paused);
        var unreadable = Connection(status: ConnectionStatus.Active, secretUnreadable: true);
        var repository = new InMemoryConnectionRepository(active, paused, unreadable);
        var connections = NewIntegrationConnections(repository);

        var resolved = await connections.ListResolvedActiveAsync(TenantId, IntegrationProviders.WhatsAppCloud.Key, Ct);

        Assert.Equal([active.Id], resolved.Select(connection => connection.Id));
        Assert.Equal(1, repository.ListCalls);
        Assert.Equal(0, repository.FindCalls);
    }

    [Fact]
    public async Task ListResolvedActiveIsEmptyWhenTheProviderIsHiddenForTheTenant()
    {
        var connections = NewIntegrationConnections(
            new InMemoryConnectionRepository(Connection(status: ConnectionStatus.Active)), modules: TenantModuleSet.Empty);

        Assert.Empty(await connections.ListResolvedActiveAsync(TenantId, IntegrationProviders.WhatsAppCloud.Key, Ct));
    }

    private IntegrationConnection Connection(
        ConnectionStatus status = ConnectionStatus.Active, bool secretUnreadable = false, string? provider = null)
    {
        var catalogProvider = new IntegrationProviderCatalog().Find(provider ?? IntegrationProviders.WhatsAppCloud.Key)
            ?? throw new InvalidOperationException("Unknown provider.");
        var isZenvia = catalogProvider.Key == IntegrationProviders.Zenvia.Key;
        var secretKey = isZenvia ? ZenviaFieldKeys.ApiToken : WhatsAppCloudFieldKeys.AccessToken;
        var connection = IntegrationConnection.Create(
            catalogProvider,
            TenantId,
            $"Conexión {Guid.CreateVersion7():N}",
            isZenvia ? ConnectionFixtures.Fields() : ConnectionFixtures.None(),
            new Dictionary<string, string>(StringComparer.Ordinal) { [secretKey] = "token-TEST" },
            _protector.Protect,
            ConnectionFixtures.MemberId,
            ConnectionFixtures.Now);

        if (status == ConnectionStatus.Paused)
        {
            connection.Pause(ConnectionFixtures.Now);
        }

        if (secretUnreadable)
        {
            _protector.Unreadable.Add((connection.Id, secretKey));
        }

        return connection;
    }

    private IntegrationConnections NewIntegrationConnections(InMemoryConnectionRepository repository, TenantModuleSet? modules = null)
    {
        var tenantModules = new FakeTenantModules();
        if (modules is not null)
        {
            tenantModules.Sets[TenantId] = modules;
        }

        return new IntegrationConnections(repository, new IntegrationProviderCatalog(), tenantModules, new FakeTenantProviders(), _protector);
    }
}
