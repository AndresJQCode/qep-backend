using Bootstrapper;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Quotations.Application;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Plan 2026-10-21 (Zenvia por tenant): qué conexiones de Zenvia le llegan a Quotations como canal. El filtro de estado,
/// visibilidad y habilitación es de <see cref="IIntegrationConnections"/>; el de este adaptador es que la conexión pueda
/// mandar una cotización, o sea que tenga plantilla.
/// </summary>
public sealed class QuotationsWhatsAppChannelDirectoryTests
{
    private const string TemplateId = "d0e79149-ed61-4784-9883-d86807f46ce5";
    private const string OrderTemplateId = "0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d";

    private static readonly Guid TenantId = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task MapsAResolvedZenviaConnection()
    {
        var id = Guid.CreateVersion7();
        var connections = new StubConnections().With(id, Fields("573001234567", TemplateId), token: "tok-1");

        var channel = Assert.IsType<ZenviaWhatsAppChannel>(
            Assert.Single(await new QuotationsWhatsAppChannelDirectory(connections).ListAvailableAsync(TenantId, Ct)));

        Assert.Equal((id, "tok-1", "573001234567", TemplateId), (channel.ConnectionId, channel.ApiToken, channel.FromNumber, channel.QuotationTemplateId));
        Assert.Null(channel.OrderTemplateId);
    }

    [Theory]
    [InlineData(OrderTemplateId, OrderTemplateId)]
    [InlineData("   ", null)]
    public async Task MapsOrderTemplateIdWhenPresent(string stored, string? expected)
    {
        var fields = Fields("573001234567", TemplateId);
        fields[ZenviaFieldKeys.OrderTemplateId] = stored;
        var connections = new StubConnections().With(Guid.CreateVersion7(), fields);

        var channel = Assert.IsType<ZenviaWhatsAppChannel>(
            Assert.Single(await new QuotationsWhatsAppChannelDirectory(connections).ListAvailableAsync(TenantId, Ct)));

        Assert.Equal(expected, channel.OrderTemplateId);
    }

    // Review Focus 1: una conexión creada antes de que existiera el campo no puede mandar nada.
    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task SkipsConnectionWithoutTemplateId(string? templateId)
    {
        var connections = new StubConnections().With(Guid.CreateVersion7(), Fields("573001234567", templateId));

        Assert.Empty(await new QuotationsWhatsAppChannelDirectory(connections).ListAvailableAsync(TenantId, Ct));
    }

    // Review Focus 2: pausada, con Zenvia deshabilitado o con un secreto que no descifra, Integrations no la resuelve.
    [Fact]
    public async Task SkipsConnectionThatDoesNotResolve()
    {
        var connections = new StubConnections().With(Guid.CreateVersion7(), Fields("573001234567", TemplateId), resolves: false);

        Assert.Empty(await new QuotationsWhatsAppChannelDirectory(connections).ListAvailableAsync(TenantId, Ct));
    }

    [Fact]
    public async Task ReturnsEveryAvailableConnection()
    {
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var connections = new StubConnections()
            .With(first, Fields("573001234567", TemplateId))
            .With(second, Fields("573009999999", TemplateId));

        var channels = await new QuotationsWhatsAppChannelDirectory(connections).ListAvailableAsync(TenantId, Ct);

        Assert.Equal([first, second], channels.Select(channel => channel.ConnectionId));
    }

    [Fact]
    public async Task AsksIntegrationsForZenviaOnly()
    {
        var connections = new StubConnections();

        await new QuotationsWhatsAppChannelDirectory(connections).ListAvailableAsync(TenantId, Ct);

        Assert.Equal([(TenantId, "zenvia")], connections.Listed);
    }

    private static Dictionary<string, string> Fields(string fromNumber, string? templateId)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal) { [ZenviaFieldKeys.FromNumber] = fromNumber };
        if (templateId is not null)
        {
            fields[ZenviaFieldKeys.TemplateId] = templateId;
        }

        return fields;
    }

    private sealed class StubConnections : IIntegrationConnections
    {
        private readonly List<ConnectionSummary> active = [];
        private readonly Dictionary<Guid, ResolvedConnection> resolved = [];

        public List<(Guid TenantId, string ProviderKey)> Listed { get; } = [];

        public StubConnections With(Guid id, Dictionary<string, string> fields, string token = "tok", bool resolves = true)
        {
            active.Add(new ConnectionSummary(id, "zenvia", $"Conexión {active.Count + 1}"));
            if (resolves)
            {
                resolved[id] = new ResolvedConnection(
                    id, "zenvia", "Conexión", fields,
                    new Dictionary<string, string>(StringComparer.Ordinal) { [ZenviaFieldKeys.ApiToken] = token });
            }

            return this;
        }

        public Task<ConnectionResolution> ResolveAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
            Task.FromResult(tenantId == TenantId && resolved.GetValueOrDefault(connectionId) is { } connection
                ? ConnectionResolution.Resolved(connection)
                : ConnectionResolution.Unusable(ConnectionUnavailableReason.NotFound, null));

        public Task<IReadOnlyList<ConnectionSummary>> ListActiveAsync(Guid tenantId, string providerKey, CancellationToken cancellationToken)
        {
            Listed.Add((tenantId, providerKey));
            return Task.FromResult<IReadOnlyList<ConnectionSummary>>(active);
        }

        public Task<IReadOnlyList<ConnectionListing>> ListByProviderAsync(Guid tenantId, string providerKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
