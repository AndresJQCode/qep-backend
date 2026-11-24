using Bootstrapper;
using Microsoft.Extensions.Options;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Messaging.Application;
using Modules.Notifications.Infrastructure;
using Modules.Support.Application;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Task-notices spec 2026-11-22 §5.4: the WhatsApp connections Support's settings offer for task notices. Unlike Wholesale's list, the ones
/// without an approved template stay in it with their template status, so the screen can tell the admin what is missing.
/// </summary>
public sealed class SupportTaskNoticeChannelsTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(null, SupportTaskNoticeTemplateStatuses.Missing)]
    [InlineData("APPROVED", SupportTaskNoticeTemplateStatuses.Approved)]
    [InlineData("PENDING", SupportTaskNoticeTemplateStatuses.Pending)]
    [InlineData("IN_APPEAL", SupportTaskNoticeTemplateStatuses.Pending)]
    [InlineData("REJECTED", SupportTaskNoticeTemplateStatuses.Rejected)]
    [InlineData("PAUSED", SupportTaskNoticeTemplateStatuses.Rejected)]
    public async Task MapsTheTemplateStatus(string? metaStatus, string expected)
    {
        var connectionId = Guid.CreateVersion7();
        var connections = new StubConnections().With(connectionId, "Ventas", "+57 300 123 4567");
        var channels = Channels(connections, new StubReadiness { [connectionId] = metaStatus });

        var listed = Assert.Single(await channels.ListAsync(TenantId, Ct));

        Assert.Equal(expected, listed.TemplateStatus);
    }

    [Fact]
    public async Task UsesTheDisplayPhoneNumber()
    {
        var withNumber = Guid.CreateVersion7();
        var withoutNumber = Guid.CreateVersion7();
        var connections = new StubConnections()
            .With(withNumber, "Ventas", "+57 300 123 4567")
            .With(withoutNumber, "Bodega", "  ");

        var listed = await Channels(connections, new StubReadiness()).ListAsync(TenantId, Ct);

        Assert.Equal(
            [
                new SupportTaskNoticeConnection(withoutNumber, "Bodega", null, SupportTaskNoticeTemplateStatuses.Missing),
                new SupportTaskNoticeConnection(withNumber, "Ventas", "+57 300 123 4567", SupportTaskNoticeTemplateStatuses.Missing),
            ],
            listed);
        Assert.Equal([(TenantId, IntegrationProviders.WhatsAppCloud.Key)], connections.Listed.Distinct());
    }

    [Fact]
    public void ExposesTheConfiguredEscalationMinutes()
    {
        var options = new NotificationsOptions { TaskNotice = new TaskNoticeOptions { EscalationAfterMinutes = 45 } };

        Assert.Equal(45, new SupportTaskNoticeChannels(new StubConnections(), new StubReadiness(), Options.Create(options)).EscalationAfterMinutes);
        Assert.Equal(30, Channels(new StubConnections(), new StubReadiness()).EscalationAfterMinutes);
    }

    private static SupportTaskNoticeChannels Channels(StubConnections connections, StubReadiness readiness) =>
        new(connections, readiness, Options.Create(new NotificationsOptions()));

    private sealed class StubReadiness : ITaskNoticeTemplateReadiness
    {
        private readonly Dictionary<Guid, string?> _statuses = [];

        public string? this[Guid connectionId]
        {
            set => _statuses[connectionId] = value;
        }

        public Task<ApprovedTemplate?> FindApprovedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The settings only read the status.");

        public Task<string?> FindStatusAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
            Task.FromResult(_statuses.GetValueOrDefault(connectionId));
    }

    private sealed class StubConnections : IIntegrationConnections
    {
        private readonly List<ResolvedConnection> _resolved = [];

        public List<(Guid TenantId, string ProviderKey)> Listed { get; } = [];

        public StubConnections With(Guid id, string name, string displayPhoneNumber)
        {
            _resolved.Add(new ResolvedConnection(
                id,
                IntegrationProviders.WhatsAppCloud.Key,
                name,
                new Dictionary<string, string>(StringComparer.Ordinal) { [WhatsAppCloudFieldKeys.DisplayPhoneNumber] = displayPhoneNumber },
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
