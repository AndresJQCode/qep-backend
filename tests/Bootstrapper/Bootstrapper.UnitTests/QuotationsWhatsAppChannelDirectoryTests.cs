using Bootstrapper;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Messaging.Application;
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
            Assert.Single(await NewDirectory(connections).ListAvailableAsync(TenantId, Ct)));

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
            Assert.Single(await NewDirectory(connections).ListAvailableAsync(TenantId, Ct)));

        Assert.Equal(expected, channel.OrderTemplateId);
    }

    // Review Focus 1: una conexión creada antes de que existiera el campo no puede mandar nada.
    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task SkipsConnectionWithoutTemplateId(string? templateId)
    {
        var connections = new StubConnections().With(Guid.CreateVersion7(), Fields("573001234567", templateId));

        Assert.Empty(await NewDirectory(connections).ListAvailableAsync(TenantId, Ct));
    }

    // Review Focus 2: pausada, con Zenvia deshabilitado o con un secreto que no descifra, Integrations no la resuelve.
    [Fact]
    public async Task SkipsConnectionThatDoesNotResolve()
    {
        var connections = new StubConnections().With(Guid.CreateVersion7(), Fields("573001234567", TemplateId), resolves: false);

        Assert.Empty(await NewDirectory(connections).ListAvailableAsync(TenantId, Ct));
    }

    [Fact]
    public async Task ReturnsEveryAvailableConnection()
    {
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var connections = new StubConnections()
            .With(first, Fields("573001234567", TemplateId))
            .With(second, Fields("573009999999", TemplateId));

        var channels = await NewDirectory(connections).ListAvailableAsync(TenantId, Ct);

        Assert.Equal([first, second], channels.Select(channel => channel.ConnectionId));
    }

    [Fact]
    public async Task AsksIntegrationsForBothProviders()
    {
        var connections = new StubConnections();

        await NewDirectory(connections).ListAvailableAsync(TenantId, Ct);

        Assert.Equal([(TenantId, "whatsapp-cloud"), (TenantId, "zenvia")], connections.Listed);
    }

    [Fact]
    public async Task ZenviaChannelCarriesTheConnectionName()
    {
        var connections = new StubConnections().With(Guid.CreateVersion7(), Fields("573001234567", TemplateId), name: "Línea de ventas");

        var channel = Assert.Single(await NewDirectory(connections).ListAvailableAsync(TenantId, Ct));

        Assert.Equal("Línea de ventas", channel.Name);
    }

    [Fact]
    public async Task MapsAMetaConnectionWithAnApprovedTemplate()
    {
        var id = Guid.CreateVersion7();
        var connections = new StubConnections().WithMeta(id, "Cuenta de Meta", "+57 300 123 4567");
        var readiness = new StubReadiness().Approve(id, new ApprovedTemplate("qep_cotizacion_v2", "es", "UTILITY", "NAMED", "[]"));

        var channel = Assert.IsType<MetaWhatsAppChannel>(
            Assert.Single(await NewDirectory(connections, readiness).ListAvailableAsync(TenantId, Ct)));

        Assert.Equal(
            (id, "Cuenta de Meta", "+57 300 123 4567", "qep_cotizacion_v2", "es"),
            (channel.ConnectionId, channel.Name, channel.Phone, channel.TemplateName, channel.TemplateLanguage));
    }

    [Fact]
    public async Task SkipsAMetaConnectionWithoutAnApprovedTemplate()
    {
        var connections = new StubConnections().WithMeta(Guid.CreateVersion7(), "Cuenta de Meta", "+57 300 123 4567");

        Assert.Empty(await NewDirectory(connections, new StubReadiness()).ListAvailableAsync(TenantId, Ct));
    }

    [Fact]
    public async Task SkipsAMetaConnectionThatDoesNotResolve()
    {
        var id = Guid.CreateVersion7();
        var connections = new StubConnections().WithMeta(id, "Cuenta de Meta", "+57 300 123 4567", resolves: false);
        var readiness = new StubReadiness().Approve(id, new ApprovedTemplate("qep_cotizacion", "es", "UTILITY", "NAMED", "[]"));

        Assert.Empty(await NewDirectory(connections, readiness).ListAvailableAsync(TenantId, Ct));
    }

    [Fact]
    public async Task ListsMetaFirstThenZenvia()
    {
        var meta = Guid.CreateVersion7();
        var zenvia = Guid.CreateVersion7();
        var connections = new StubConnections()
            .With(zenvia, Fields("573001234567", TemplateId))
            .WithMeta(meta, "Cuenta de Meta", null);
        var readiness = new StubReadiness().Approve(meta, new ApprovedTemplate("qep_cotizacion", "es", "UTILITY", "NAMED", "[]"));

        var channels = await NewDirectory(connections, readiness).ListAvailableAsync(TenantId, Ct);

        Assert.Equal([meta, zenvia], channels.Select(channel => channel.ConnectionId));
        Assert.Null(channels[0].Phone);
    }

    private static QuotationsWhatsAppChannelDirectory NewDirectory(StubConnections connections, StubReadiness? readiness = null) =>
        new(connections, readiness ?? new StubReadiness());

    private static Dictionary<string, string> Fields(string fromNumber, string? templateId)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal) { [ZenviaFieldKeys.FromNumber] = fromNumber };
        if (templateId is not null)
        {
            fields[ZenviaFieldKeys.TemplateId] = templateId;
        }

        return fields;
    }

    private sealed class StubReadiness : IQuotationTemplateReadiness
    {
        private readonly Dictionary<Guid, ApprovedTemplate> approved = [];

        public StubReadiness Approve(Guid connectionId, ApprovedTemplate template)
        {
            approved[connectionId] = template;
            return this;
        }

        public Task<ApprovedTemplate?> FindApprovedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
            Task.FromResult(tenantId == TenantId ? approved.GetValueOrDefault(connectionId) : null);
    }

    private sealed class StubConnections : IIntegrationConnections
    {
        private readonly List<ConnectionSummary> active = [];
        private readonly Dictionary<Guid, ResolvedConnection> resolved = [];

        public List<(Guid TenantId, string ProviderKey)> Listed { get; } = [];

        public StubConnections With(Guid id, Dictionary<string, string> fields, string token = "tok", bool resolves = true, string name = "Conexión")
        {
            active.Add(new ConnectionSummary(id, "zenvia", $"Conexión {active.Count + 1}"));
            if (resolves)
            {
                resolved[id] = new ResolvedConnection(
                    id, "zenvia", name, fields,
                    new Dictionary<string, string>(StringComparer.Ordinal) { [ZenviaFieldKeys.ApiToken] = token });
            }

            return this;
        }

        public StubConnections WithMeta(Guid id, string name, string? displayPhoneNumber, bool resolves = true)
        {
            active.Add(new ConnectionSummary(id, "whatsapp-cloud", $"Conexión {active.Count + 1}"));
            if (resolves)
            {
                var fields = new Dictionary<string, string>(StringComparer.Ordinal);
                if (displayPhoneNumber is not null)
                {
                    fields[WhatsAppCloudFieldKeys.DisplayPhoneNumber] = displayPhoneNumber;
                }

                resolved[id] = new ResolvedConnection(
                    id, "whatsapp-cloud", name, fields, new Dictionary<string, string>(StringComparer.Ordinal));
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
            return Task.FromResult<IReadOnlyList<ConnectionSummary>>(active.Where(summary => summary.ProviderKey == providerKey).ToList());
        }

        public Task<IReadOnlyList<ConnectionListing>> ListByProviderAsync(Guid tenantId, string providerKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
