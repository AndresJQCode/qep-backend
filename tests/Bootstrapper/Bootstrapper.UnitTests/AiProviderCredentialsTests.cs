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

        var resolution = await new AiProviderCredentials(connections, new StubReporter()).ResolveAsync(TenantId, AiProviders.Anthropic, Ct);

        Assert.Null(resolution.Unavailable);
        Assert.Equal((id, "sk-ant-SENTINEL"), (resolution.Credential?.ConnectionId, resolution.Credential?.ApiKey));
        Assert.DoesNotContain("SENTINEL", resolution.Credential!.ToString(), StringComparison.Ordinal);
        Assert.Equal((TenantId, id), connections.Resolved);
    }

    [Fact]
    public async Task WithoutAConnectionItIsNotConfigured()
    {
        var resolution = await new AiProviderCredentials(new StubConnections(), new StubReporter()).ResolveAsync(TenantId, AiProviders.Anthropic, Ct);

        Assert.Equal(AiCredentialUnavailableReason.NotConfigured, resolution.Unavailable);
        Assert.Null(resolution.Credential);
    }

    // C-19: la conexión de un proveedor no habilita al otro.
    [Fact]
    public async Task OpenAiIsNotConfiguredEvenWithAnAnthropicConnection()
    {
        var connections = new StubConnections
        {
            Listings = { ["anthropic"] = [new ConnectionListing(Guid.CreateVersion7(), "Anthropic", ConnectionStatus.Active)] },
        };

        var resolution = await new AiProviderCredentials(connections, new StubReporter()).ResolveAsync(TenantId, AiProviders.OpenAi, Ct);

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

        var resolution = await new AiProviderCredentials(connections, new StubReporter()).ResolveAsync(TenantId, AiProviders.Anthropic, Ct);

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

        var resolution = await new AiProviderCredentials(connections, new StubReporter()).ResolveAsync(TenantId, AiProviders.Anthropic, Ct);

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

        var states = await new AiProviderCredentials(connections, new StubReporter()).DescribeAsync(TenantId, Ct);

        Assert.Equal(
            [
                new AiProviderConnectionState(AiProviders.Anthropic, Connectable: true, id, "NeedsAttention"),
                new AiProviderConnectionState(AiProviders.OpenAi, Connectable: true, null, null),
            ],
            states);
    }

    // Plan 2026-11-17 A1: Integrations no limpia last_failure_code al verificar (IntegrationConnection.MarkVerified), así que el código
    // sólo viaja mientras la conexión está en NeedsAttention; en una conexión sana sería un fallo viejo.
    [Theory]
    [InlineData(ConnectionStatus.NeedsAttention, "quota_exhausted")]
    [InlineData(ConnectionStatus.Active, null)]
    [InlineData(ConnectionStatus.Paused, null)]
    public async Task DescribeCarriesTheFailureCodeOnlyWhileTheConnectionNeedsAttention(ConnectionStatus status, string? expected)
    {
        var id = Guid.CreateVersion7();
        var connections = new StubConnections
        {
            Listings = { ["anthropic"] = [new ConnectionListing(id, "Anthropic", status, LastFailureCode: "quota_exhausted")] },
        };

        var states = await new AiProviderCredentials(connections, new StubReporter()).DescribeAsync(TenantId, Ct);

        Assert.Equal(new AiProviderConnectionState(AiProviders.Anthropic, Connectable: true, id, status.ToString(), FailureCode: expected), states[0]);
        Assert.Null(states[1].FailureCode);
    }

    // Tarea 6: OpenAI es un proveedor de Integrations con la misma forma; su clave se resuelve igual, de su propia conexión.
    [Fact]
    public async Task ResolveReturnsTheKeyOfTheActiveOpenAiConnection()
    {
        var id = Guid.CreateVersion7();
        var connections = new StubConnections
        {
            Listings = { ["openai"] = [new ConnectionListing(id, "Cuenta de OpenAI", ConnectionStatus.Active)] },
            Resolution = ConnectionResolution.Resolved(new ResolvedConnection(
                id, "openai", "Cuenta de OpenAI", new Dictionary<string, string>(),
                new Dictionary<string, string> { ["apiKey"] = "sk-proj-SENTINEL" })),
        };

        var resolution = await new AiProviderCredentials(connections, new StubReporter()).ResolveAsync(TenantId, AiProviders.OpenAi, Ct);

        Assert.Null(resolution.Unavailable);
        Assert.Equal((id, "sk-proj-SENTINEL"), (resolution.Credential?.ConnectionId, resolution.Credential?.ApiKey));
    }

    [Fact]
    public void TheOpenAiProviderKeyMatchesIntegrations() =>
        Assert.Equal(IntegrationProviders.OpenAi.Key, AiProviders.OpenAi.Value);

    // El adaptador nombra el proveedor de Integrations con la misma clave que Ai: si alguna diverge, la conexión nunca se encuentra.
    [Fact]
    public void TheAnthropicProviderKeyMatchesIntegrations() =>
        Assert.Equal(IntegrationProviders.Anthropic.Key, AiProviders.Anthropic.Value);

    // C-19: un rechazo definitivo de la cuenta deja la conexión de ESE proveedor en NeedsAttention, con el motivo, por el camino de
    // Integrations (auditoría y evento en la misma transacción).
    [Theory]
    [InlineData(AiCredentialFailures.CredentialsRejected)]
    [InlineData(AiCredentialFailures.PermissionDenied)]
    [InlineData(AiCredentialFailures.QuotaExhausted)]
    public async Task MarkNeedsAttentionReportsTheConnectionWithItsFailureCode(string failureCode)
    {
        var reporter = new StubReporter();
        var connectionId = Guid.CreateVersion7();

        await new AiProviderCredentials(new StubConnections(), reporter).MarkNeedsAttentionAsync(TenantId, connectionId, failureCode, Ct);

        Assert.Equal([(TenantId, connectionId, failureCode)], reporter.Reported);
    }

    private sealed class StubReporter : IConnectionHealthReporter
    {
        public List<(Guid TenantId, Guid ConnectionId, string FailureCode)> Reported { get; } = [];

        public Task ReportCredentialsRejectedAsync(Guid tenantId, Guid connectionId, string failureCode, CancellationToken cancellationToken)
        {
            Reported.Add((tenantId, connectionId, failureCode));
            return Task.CompletedTask;
        }

        public Task ReportTokenExpiredAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubConnections : IIntegrationConnections
    {
        public Dictionary<string, IReadOnlyList<ConnectionListing>> Listings { get; } = new(StringComparer.Ordinal);

        public ConnectionResolution? Resolution { get; init; }

        public (Guid TenantId, Guid ConnectionId)? Resolved { get; private set; }

        public Task<IReadOnlyList<ConnectionListing>> ListByProviderAsync(Guid tenantId, string providerKey, CancellationToken cancellationToken) =>
            Task.FromResult(Listings.TryGetValue(providerKey, out var listings) ? listings : []);

        public Task<ConnectionResolution> ResolveForSetupAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ConnectionResolution> ResolveAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
        {
            Resolved = (tenantId, connectionId);
            return Task.FromResult(Resolution ?? ConnectionResolution.Unusable(ConnectionUnavailableReason.NotFound, null));
        }

        public Task<IReadOnlyList<ConnectionSummary>> ListActiveAsync(Guid tenantId, string providerKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ResolvedConnection>> ListResolvedActiveAsync(Guid tenantId, string providerKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ConnectionListing>> ListByProvidersAsync(
            Guid tenantId, IReadOnlyCollection<string> providerKeys, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
