using Modules.ServiceContracts.Application;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Cobros recurrentes §5: el adaptador de <see cref="IServiceContractsWhatsAppOutbound"/> hacia Mensajería. Elige la plantilla por tipo de aviso,
/// arma las variables posicionales ya formateadas y traduce los resultados del envío a los <c>failure_code</c> de §3.4.
/// </summary>
public sealed class ServiceContractsMetaWhatsAppSenderTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ConnectionId = Guid.CreateVersion7();
    private static readonly Guid CustomerId = Guid.CreateVersion7();
    private static readonly Guid MemberId = Guid.CreateVersion7();
    private static readonly ServiceContractsWhatsAppContext Context = new(TenantId, CustomerId, MemberId);
    private static readonly ServiceContractsEmailFormat Format = new("America/Bogota", "dd/MM/yyyy", "1.234,56");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ServiceContractsCustomerMessage Reminder(
        IReadOnlyList<ServiceContractsMessageLine> lines, IReadOnlyList<CurrencyAmountDto> totals, string name = "Ana Pérez") =>
        new(ServiceContractsMessageKind.Reminder, "+573001234567", name, "Verde SAS", lines, totals, new DateOnly(2026, 3, 10), Format);

    private static ServiceContractsMessageLine Line(string service, decimal amount, string currency = "COP", int daysOverdue = 0) =>
        new(service, amount, currency, new DateOnly(2026, 3, 10), daysOverdue);

    [Fact]
    public async Task AReminderFillsCustomerServiceAmountAndDate()
    {
        var rig = new Rig();

        var result = await rig.Sender.SendAsync(
            Context, Reminder([Line("Hosting plan pro", 150000m)], [new CurrencyAmountDto("COP", 150000m)]), ConnectionId, Ct);

        Assert.IsType<ServiceContractsSendResult.Sent>(result);
        var send = Assert.Single(rig.Outbound.Sent);
        Assert.Equal(["Ana Pérez", "Hosting plan pro", "$ 150.000", "10/03/2026"], send.PositionalParameters);
        Assert.Equal(("qep_cobro_recordatorio", "es"), (send.TemplateName, send.TemplateLanguage));
        Assert.Equal((TenantId, ConnectionId, "573001234567"), (send.TenantId, send.ConnectionId, send.WaId));
        Assert.Equal((CustomerId, MemberId, "Ana Pérez"), (send.CustomerId, send.SentByMemberId, send.RecipientName));
    }

    [Fact]
    public async Task AReminderWithTwoServicesJoinsNamesAndTotalsWithY()
    {
        var rig = new Rig();

        await rig.Sender.SendAsync(
            Context,
            Reminder(
                [Line("Hosting plan pro", 150000m), Line("Dominio", 20m, "USD")],
                [new CurrencyAmountDto("COP", 150000m), new CurrencyAmountDto("USD", 20m)]),
            ConnectionId, Ct);

        var send = Assert.Single(rig.Outbound.Sent);
        Assert.Equal("Hosting plan pro y Dominio", send.PositionalParameters[1]);
        Assert.Equal("$ 150.000 y US$ 20,00", send.PositionalParameters[2]);
    }

    [Fact]
    public async Task AnOverdueMessageFillsCountAndTotal()
    {
        var rig = new Rig();
        var message = Reminder(
            [Line("Hosting", 300000m, daysOverdue: 5), Line("Dominio", 150000m, daysOverdue: 2)], [new CurrencyAmountDto("COP", 450000m)]) with
        {
            Kind = ServiceContractsMessageKind.Overdue,
        };

        await rig.Sender.SendAsync(Context, message, ConnectionId, Ct);

        var send = Assert.Single(rig.Outbound.Sent);
        Assert.Equal(("qep_cobro_vencido", "es"), (send.TemplateName, send.TemplateLanguage));
        Assert.Equal(["Ana Pérez", "2", "$ 450.000"], send.PositionalParameters);
    }

    [Fact]
    public async Task ConnectionUnavailableAndFailedMapToTheFailureCodes()
    {
        var message = Reminder([Line("Hosting", 150000m)], [new CurrencyAmountDto("COP", 150000m)]);

        var paused = new Rig { Result = new BillingTemplateSendResult.ConnectionUnavailable("paused") };
        var rejected = new Rig { Result = new BillingTemplateSendResult.Failed("132018") };

        Assert.Equal("connection_unavailable:paused", Assert.IsType<ServiceContractsSendResult.Failed>(await paused.Sender.SendAsync(Context, message, ConnectionId, Ct)).Code);
        Assert.Equal("send_failed:132018", Assert.IsType<ServiceContractsSendResult.Failed>(await rejected.Sender.SendAsync(Context, message, ConnectionId, Ct)).Code);
    }

    [Fact]
    public async Task ATemplateThatIsNotApprovedFailsWithoutSending()
    {
        var rig = new Rig { ApproveByDefault = false };

        var result = await rig.Sender.SendAsync(
            Context, Reminder([Line("Hosting", 150000m)], [new CurrencyAmountDto("COP", 150000m)]), ConnectionId, Ct);

        Assert.Equal("template_not_approved", Assert.IsType<ServiceContractsSendResult.Failed>(result).Code);
        Assert.Empty(rig.Outbound.Sent);
    }

    [Fact]
    public async Task ANameWithLineBreaksAndLongSpacesGoesOutOnOneLine()
    {
        var rig = new Rig();

        await rig.Sender.SendAsync(
            Context, Reminder([Line("Hosting", 150000m)], [new CurrencyAmountDto("COP", 150000m)], "Ana\r\n   Pérez      Gómez"), ConnectionId, Ct);

        Assert.Equal("Ana Pérez Gómez", Assert.Single(rig.Outbound.Sent).PositionalParameters[0]);
    }

    [Fact]
    public async Task ListConnectionsMarksReadyOnlyWithBothTemplatesApproved()
    {
        var both = Guid.CreateVersion7();
        var onlyReminder = Guid.CreateVersion7();
        var unresolved = Guid.CreateVersion7();
        var rig = new Rig { ApproveByDefault = false };
        rig.Integrations.WithMeta(both, "Línea principal", "+57 300 111 2233");
        rig.Integrations.WithMeta(onlyReminder, "Línea de pruebas", null);
        rig.Integrations.WithMeta(unresolved, "Línea caída", null, resolves: false);
        rig.Readiness.Approve(both, MessageTemplateKind.BillingReminder);
        rig.Readiness.Approve(both, MessageTemplateKind.BillingOverdue);
        rig.Readiness.Approve(onlyReminder, MessageTemplateKind.BillingReminder);

        var connections = await rig.Sender.ListConnectionsAsync(TenantId, Ct);

        Assert.Equal(
            [
                new ServiceContractsConnectionDto(both, "Línea principal", "+57 300 111 2233", true, null),
                new ServiceContractsConnectionDto(onlyReminder, "Línea de pruebas", null, false, "template_not_approved"),
                new ServiceContractsConnectionDto(unresolved, "Línea caída", null, false, "connection_unavailable"),
            ],
            connections);
    }

    private sealed class Rig
    {
        public Rig()
        {
            Outbound = new RecordingOutbound(this);
            Readiness = new StubReadiness(this);
            Sender = new ServiceContractsMetaWhatsAppSender(Outbound, Readiness, Integrations);
        }

        public BillingTemplateSendResult Result { get; init; } = new BillingTemplateSendResult.Sent(Guid.NewGuid(), Guid.NewGuid());

        /// <summary>Sin aprobaciones declaradas con <c>Approve</c>, responde si las dos plantillas de cualquier conexión están aprobadas.</summary>
        public bool ApproveByDefault { get; init; } = true;

        public RecordingOutbound Outbound { get; }

        public StubReadiness Readiness { get; }

        public StubConnections Integrations { get; } = new();

        public ServiceContractsMetaWhatsAppSender Sender { get; }
    }

    private sealed class RecordingOutbound(Rig rig) : IBillingTemplateOutbound
    {
        public List<BillingTemplateSend> Sent { get; } = [];

        public Task<BillingTemplateSendResult> SendAsync(BillingTemplateSend send, CancellationToken cancellationToken)
        {
            Sent.Add(send);
            return Task.FromResult(rig.Result);
        }
    }

    private sealed class StubReadiness(Rig rig) : IBillingTemplateReadiness
    {
        private readonly HashSet<(Guid, MessageTemplateKind)> approved = [];

        public void Approve(Guid connectionId, MessageTemplateKind kind) => approved.Add((connectionId, kind));

        public Task<ApprovedBillingTemplate?> FindApprovedAsync(
            Guid tenantId, Guid connectionId, MessageTemplateKind kind, CancellationToken cancellationToken)
        {
            var isApproved = approved.Count == 0 ? rig.ApproveByDefault : approved.Contains((connectionId, kind));
            var template = isApproved && tenantId == TenantId
                ? new ApprovedBillingTemplate(BillingTemplateDefinitions.For(kind).Name, BillingTemplateDefinitions.Language)
                : null;
            return Task.FromResult(template);
        }
    }

    private sealed class StubConnections : IIntegrationConnections
    {
        private readonly List<ConnectionSummary> active = [];
        private readonly Dictionary<Guid, ResolvedConnection> resolved = [];

        public StubConnections WithMeta(Guid id, string name, string? displayPhoneNumber, bool resolves = true)
        {
            active.Add(new ConnectionSummary(id, "whatsapp-cloud", name));
            if (resolves)
            {
                var fields = new Dictionary<string, string>(StringComparer.Ordinal);
                if (displayPhoneNumber is not null)
                {
                    fields[WhatsAppCloudFieldKeys.DisplayPhoneNumber] = displayPhoneNumber;
                }

                resolved[id] = new ResolvedConnection(id, "whatsapp-cloud", name, fields, new Dictionary<string, string>(StringComparer.Ordinal));
            }

            return this;
        }

        public Task<ConnectionResolution> ResolveAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
            Task.FromResult(tenantId == TenantId && resolved.GetValueOrDefault(connectionId) is { } connection
                ? ConnectionResolution.Resolved(connection)
                : ConnectionResolution.Unusable(ConnectionUnavailableReason.NotFound, null));

        public Task<IReadOnlyList<ConnectionSummary>> ListActiveAsync(Guid tenantId, string providerKey, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ConnectionSummary>>(
                tenantId == TenantId ? active.Where(summary => summary.ProviderKey == providerKey).ToList() : []);

        public Task<IReadOnlyList<ConnectionListing>> ListByProviderAsync(Guid tenantId, string providerKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
