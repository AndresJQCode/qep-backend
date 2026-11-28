using Bootstrapper;
using Modules.Hr.Application.BirthdayNotices;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Messaging.Application;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Spec 2026-11-26 §5.1: HR asks whether a connection id is a usable WhatsApp connection of the tenant. Integrations already
/// filters state, visibility and provider enablement; the adapter only answers membership of that list. Spec 2026-11-27: it also
/// lists those connections with the status of the <c>HrBirthdayNotice</c> template, for the settings screen.
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
        var adapter = new HrWhatsAppConnections(connections, new StubReadiness());

        Assert.True(await adapter.IsWhatsAppConnectionAsync(TenantId, active, Ct));

        // Not listed as active (inactive, hidden, other provider or unknown) is not valid.
        Assert.False(await adapter.IsWhatsAppConnectionAsync(TenantId, Guid.CreateVersion7(), Ct));

        // The same id asked from another tenant is not valid: the list is per tenant.
        Assert.False(await adapter.IsWhatsAppConnectionAsync(Guid.CreateVersion7(), active, Ct));

        Assert.All(connections.Listed, call => Assert.Equal(IntegrationProviders.WhatsAppCloud.Key, call.ProviderKey));
    }

    [Fact]
    public async Task ListsTheActiveConnectionsWithTheBirthdayTemplateStatusOrderedByName()
    {
        Guid approved = Guid.CreateVersion7(), pending = Guid.CreateVersion7(), appeal = Guid.CreateVersion7(),
            rejected = Guid.CreateVersion7(), missing = Guid.CreateVersion7();
        var connections = new StubConnections()
            .With(approved, IntegrationProviders.WhatsAppCloud.Key, "ventas", "+57 300 111 2233")
            .With(pending, IntegrationProviders.WhatsAppCloud.Key, "Bodega", "   ")
            .With(appeal, IntegrationProviders.WhatsAppCloud.Key, "Cartera")
            .With(rejected, IntegrationProviders.WhatsAppCloud.Key, "Zona norte")
            .With(missing, IntegrationProviders.WhatsAppCloud.Key, "Atención");
        var readiness = new StubReadiness()
            .Status(approved, "APPROVED").Status(pending, "PENDING").Status(appeal, "IN_APPEAL").Status(rejected, "PAUSED");
        var adapter = new HrWhatsAppConnections(connections, readiness);

        var listed = await adapter.ListAsync(TenantId, Ct);

        // By name ignoring case, as Support lists them; a blank number is no number.
        Assert.Equal(
            [
                new HrWhatsAppConnection(missing, "Atención", null, HrBirthdayNoticeTemplateStatuses.Missing),
                new HrWhatsAppConnection(pending, "Bodega", null, HrBirthdayNoticeTemplateStatuses.Pending),
                new HrWhatsAppConnection(appeal, "Cartera", null, HrBirthdayNoticeTemplateStatuses.Pending),
                new HrWhatsAppConnection(approved, "ventas", "+57 300 111 2233", HrBirthdayNoticeTemplateStatuses.Approved),
                new HrWhatsAppConnection(rejected, "Zona norte", null, HrBirthdayNoticeTemplateStatuses.Rejected),
            ],
            listed);
        Assert.All(readiness.Asked, tenant => Assert.Equal(TenantId, tenant));

        // Another tenant sees none of them.
        Assert.Empty(await adapter.ListAsync(Guid.CreateVersion7(), Ct));
    }

    private sealed class StubReadiness : IHrBirthdayNoticeTemplateReadiness
    {
        private readonly Dictionary<Guid, string> _statuses = [];

        public List<Guid> Asked { get; } = [];

        public StubReadiness Status(Guid connectionId, string metaStatus)
        {
            _statuses[connectionId] = metaStatus;
            return this;
        }

        public Task<ApprovedTemplate?> FindApprovedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<string?> FindStatusAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
        {
            Asked.Add(tenantId);
            return Task.FromResult(_statuses.TryGetValue(connectionId, out var status) ? status : null);
        }
    }

    private sealed class StubConnections : IIntegrationConnections
    {
        private readonly List<ResolvedConnection> _resolved = [];

        public List<(Guid TenantId, string ProviderKey)> Listed { get; } = [];

        public StubConnections With(Guid id, string providerKey, string name = "Ventas", string? phone = null)
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            if (phone is not null)
            {
                fields[WhatsAppCloudFieldKeys.DisplayPhoneNumber] = phone;
            }

            _resolved.Add(new ResolvedConnection(
                id,
                providerKey,
                name,
                fields,
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
