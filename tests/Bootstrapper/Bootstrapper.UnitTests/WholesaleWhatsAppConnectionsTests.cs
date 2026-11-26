using Bootstrapper;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Messaging.Application;
using Modules.Wholesale.Application;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Spec 2026-11-18: las conexiones con que Mayorista puede mandar el aviso. Integrations ya filtra estado, visibilidad y
/// habilitación; acá queda la que tiene la plantilla del aviso aprobada, nombrada por su número (D-7).
/// </summary>
public sealed class WholesaleWhatsAppConnectionsTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly ApprovedTemplate Template = new("qep_mayorista_invitacion", "es", "UTILITY", "NAMED", "[]");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ListReadyKeepsOnlyConnectionsWithTheApprovedTemplateAndLabelsThemByNumber()
    {
        var withNumber = Guid.CreateVersion7();
        var notApproved = Guid.CreateVersion7();
        var withoutNumber = Guid.CreateVersion7();
        var connections = new StubConnections()
            .With(withNumber, "Ventas", "+57 300 123 4567")
            .With(notApproved, "Soporte", "+57 300 000 0000")
            .With(withoutNumber, "Bodega", "  ");
        var readiness = new StubReadiness(withNumber, withoutNumber);

        var ready = await new WholesaleWhatsAppConnections(connections, readiness).ListReadyAsync(TenantId, Ct);

        Assert.Equal(
            [new WholesaleWhatsAppConnection(withNumber, "+57 300 123 4567"), new WholesaleWhatsAppConnection(withoutNumber, "Bodega")],
            ready);
        Assert.Equal([(TenantId, IntegrationProviders.WhatsAppCloud.Key)], connections.Listed);
    }

    // D-7: el label de un aviso sale de la conexión activa; una que ya no se puede usar no tiene label (el detalle dice «Sin nombre»).
    [Fact]
    public async Task FindLabelIsTheNumberOfAnActiveConnectionAndNullOtherwise()
    {
        var active = Guid.CreateVersion7();
        var connections = new StubConnections().With(active, "Ventas", "+57 300 123 4567");
        var adapter = new WholesaleWhatsAppConnections(connections, new StubReadiness());

        Assert.Equal("+57 300 123 4567", await adapter.FindLabelAsync(TenantId, active, Ct));
        Assert.Null(await adapter.FindLabelAsync(TenantId, Guid.CreateVersion7(), Ct));
    }

    private sealed class StubReadiness(params Guid[] approved) : IWholesaleInvitationNoticeReadiness
    {
        public Task<ApprovedTemplate?> FindApprovedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
            Task.FromResult(tenantId == TenantId && approved.Contains(connectionId) ? Template : null);
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
            Task.FromResult(tenantId == TenantId && _resolved.FirstOrDefault(connection => connection.Id == connectionId) is { } found
                ? ConnectionResolution.Resolved(found)
                : ConnectionResolution.Unusable(ConnectionUnavailableReason.NotFound, null));

        public Task<IReadOnlyList<ConnectionSummary>> ListActiveAsync(Guid tenantId, string providerKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ConnectionListing>> ListByProviderAsync(Guid tenantId, string providerKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ConnectionListing>> ListByProvidersAsync(
            Guid tenantId, IReadOnlyCollection<string> providerKeys, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
