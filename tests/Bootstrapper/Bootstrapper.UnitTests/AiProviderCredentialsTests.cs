using Bootstrapper;
using Modules.Ai.Application;
using Modules.Ai.Domain;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Plan 2026-11-12, C-1 y C-19: la clave de cada proveedor de modelos es la única conexión de ese proveedor en Integrations. Ai no ve
/// Integrations (AiLayerTests); el adaptador traduce la conexión y su estado al puerto de Ai, y la clave sólo viaja resuelta de una activa.
/// </summary>
public sealed class AiProviderCredentialsTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ResolveReturnsTheKeyOfTheActiveConnection()
    {
        var id = Guid.CreateVersion7();
        var connections = new StubConnections
        {
            Listings = { ["anthropic"] = [new ConnectionListing(id, "Cuenta de Anthropic", ConnectionStatus.Active)] },
            Resolution = ConnectionResolution.Resolved(new ResolvedConnection(
                id, "anthropic", "Cuenta de Anthropic", new Dictionary<string, string>(),
                new Dictionary<string, string> { ["apiKey"] = "sk-ant-SENTINEL" })),
        };

        var resolution = await new AiProviderCredentials(connections).ResolveAsync(TenantId, AiProviders.Anthropic, Ct);

        Assert.Null(resolution.Unavailable);
        Assert.Equal((id, "sk-ant-SENTINEL"), (resolution.Credential?.ConnectionId, resolution.Credential?.ApiKey));
        Assert.DoesNotContain("SENTINEL", resolution.Credential!.ToString(), StringComparison.Ordinal);
        Assert.Equal((TenantId, id), connections.Resolved);
    }

    [Fact]
    public async Task WithoutAConnectionItIsNotConfigured()
    {
        var resolution = await new AiProviderCredentials(new StubConnections()).ResolveAsync(TenantId, AiProviders.Anthropic, Ct);

        Assert.Equal(AiCredentialUnavailableReason.NotConfigured, resolution.Unavailable);
        Assert.Null(resolution.Credential);
    }

    // C-19: la conexión de un proveedor no habilita al otro. OpenAI no es todavía un proveedor de Integrations (Tarea 6).
    [Fact]
    public async Task OpenAiIsNotConfiguredEvenWithAnAnthropicConnection()
    {
        var connections = new StubConnections
        {
            Listings = { ["anthropic"] = [new ConnectionListing(Guid.CreateVersion7(), "Anthropic", ConnectionStatus.Active)] },
        };

        var resolution = await new AiProviderCredentials(connections).ResolveAsync(TenantId, AiProviders.OpenAi, Ct);

        Assert.Equal(AiCredentialUnavailableReason.NotConfigured, resolution.Unavailable);
    }

    [Theory]
    [InlineData(ConnectionUnavailableReason.Paused, AiCredentialUnavailableReason.Paused)]
    [InlineData(ConnectionUnavailableReason.NeedsAttention, AiCredentialUnavailableReason.NeedsAttention)]
    [InlineData(ConnectionUnavailableReason.SecretUnreadable, AiCredentialUnavailableReason.SecretUnreadable)]
    [InlineData(ConnectionUnavailableReason.ProviderHidden, AiCredentialUnavailableReason.NotConfigured)]
    [InlineData(ConnectionUnavailableReason.ProviderDisabled, AiCredentialUnavailableReason.NotConfigured)]
    [InlineData(ConnectionUnavailableReason.NotFound, AiCredentialUnavailableReason.NotConfigured)]
    public async Task AnUnusableConnectionCarriesItsReasonWithoutTheKey(ConnectionUnavailableReason reason, AiCredentialUnavailableReason expected)
    {
        var id = Guid.CreateVersion7();
        var connections = new StubConnections
        {
            Listings = { ["anthropic"] = [new ConnectionListing(id, "Anthropic", ConnectionStatus.Active)] },
            Resolution = ConnectionResolution.Unusable(reason, "Anthropic"),
        };

        var resolution = await new AiProviderCredentials(connections).ResolveAsync(TenantId, AiProviders.Anthropic, Ct);

        Assert.Equal(expected, resolution.Unavailable);
        Assert.Null(resolution.Credential);
    }

    // Una clave vacía no es una clave: no se manda al proveedor.
    [Fact]
    public async Task AnEmptyKeyIsSecretUnreadable()
    {
        var id = Guid.CreateVersion7();
        var connections = new StubConnections
        {
            Listings = { ["anthropic"] = [new ConnectionListing(id, "Anthropic", ConnectionStatus.Active)] },
            Resolution = ConnectionResolution.Resolved(new ResolvedConnection(
                id, "anthropic", "Anthropic", new Dictionary<string, string>(), new Dictionary<string, string>())),
        };

        var resolution = await new AiProviderCredentials(connections).ResolveAsync(TenantId, AiProviders.Anthropic, Ct);

        Assert.Equal(AiCredentialUnavailableReason.SecretUnreadable, resolution.Unavailable);
    }

    // BFF de GET /ai/settings: un estado por proveedor, en el orden de AiProviders.All, y si el proveedor se puede conectar hoy.
    [Fact]
    public async Task DescribeListsEveryProviderWithItsConnectionState()
    {
        var id = Guid.CreateVersion7();
        var connections = new StubConnections
        {
            Listings = { ["anthropic"] = [new ConnectionListing(id, "Anthropic", ConnectionStatus.NeedsAttention)] },
        };

        var states = await new AiProviderCredentials(connections).DescribeAsync(TenantId, Ct);

        Assert.Equal(
            [
                new AiProviderConnectionState(AiProviders.Anthropic, Connectable: true, id, "NeedsAttention"),
                new AiProviderConnectionState(AiProviders.OpenAi, Connectable: false, null, null),
            ],
            states);
    }

    // El adaptador nombra el proveedor de Integrations con la misma clave que Ai: si alguna diverge, la conexión nunca se encuentra.
    [Fact]
    public void TheAnthropicProviderKeyMatchesIntegrations() =>
        Assert.Equal(IntegrationProviders.Anthropic.Key, AiProviders.Anthropic.Value);

    private sealed class StubConnections : IIntegrationConnections
    {
        public Dictionary<string, IReadOnlyList<ConnectionListing>> Listings { get; } = new(StringComparer.Ordinal);

        public ConnectionResolution? Resolution { get; init; }

        public (Guid TenantId, Guid ConnectionId)? Resolved { get; private set; }

        public Task<IReadOnlyList<ConnectionListing>> ListByProviderAsync(Guid tenantId, string providerKey, CancellationToken cancellationToken) =>
            Task.FromResult(Listings.TryGetValue(providerKey, out var listings) ? listings : []);

        public Task<ConnectionResolution> ResolveAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
        {
            Resolved = (tenantId, connectionId);
            return Task.FromResult(Resolution ?? ConnectionResolution.Unusable(ConnectionUnavailableReason.NotFound, null));
        }

        public Task<IReadOnlyList<ConnectionSummary>> ListActiveAsync(Guid tenantId, string providerKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
