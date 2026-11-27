using Bootstrapper;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Spec 2026-11-26 §5.1: HR asks whether a connection id is a usable WhatsApp connection of the tenant. Integrations already
/// filters state, visibility and provider enablement; the adapter only answers membership of that list.
/// </summary>
public sealed class HrWhatsAppConnectionsTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OnlyActiveWhatsAppConnectionsOfTheTenantAreValid()
    {
        var active = Guid.CreateVersion7();
        var connections = new StubConnections().With(active, IntegrationProviders.WhatsAppCloud.Key);
        var adapter = new HrWhatsAppConnections(connections);

        Assert.True(await adapter.IsWhatsAppConnectionAsync(TenantId, active, Ct));

        // Not listed as active (inactive, hidden, other provider or unknown) is not valid.
        Assert.False(await adapter.IsWhatsAppConnectionAsync(TenantId, Guid.CreateVersion7(), Ct));

        // The same id asked from another tenant is not valid: the list is per tenant.
        Assert.False(await adapter.IsWhatsAppConnectionAsync(Guid.CreateVersion7(), active, Ct));

        Assert.All(connections.Listed, call => Assert.Equal(IntegrationProviders.WhatsAppCloud.Key, call.ProviderKey));
    }

    private sealed class StubConnections : IIntegrationConnections
    {
        private readonly List<ResolvedConnection> _resolved = [];

        public List<(Guid TenantId, string ProviderKey)> Listed { get; } = [];

        public StubConnections With(Guid id, string providerKey)
        {
            _resolved.Add(new ResolvedConnection(
                id,
                providerKey,
                "Ventas",
                new Dictionary<string, string>(StringComparer.Ordinal),
                new Dictionary<string, string>(StringComparer.Ordinal)));
            return this;
        }

        public Task<IReadOnlyList<ResolvedConnection>> ListResolvedActiveAsync(
            Guid tenantId, string providerKey, CancellationToken cancellationToken)
        {
            Listed.Add((tenantId, providerKey));
            return Task.FromResult<IReadOnlyList<ResolvedConnection>>(
                tenantId == TenantId ? _resolved.Where(connection => connection.ProviderKey == providerKey).ToList() : []);
        }

        public Task<ConnectionResolution> ResolveForSetupAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ConnectionResolution> ResolveAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ConnectionSummary>> ListActiveAsync(Guid tenantId, string providerKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ConnectionListing>> ListByProviderAsync(Guid tenantId, string providerKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ConnectionListing>> ListByProvidersAsync(
            Guid tenantId, IReadOnlyCollection<string> providerKeys, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
