using Bootstrapper;
using Modules.Messaging.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;
using Modules.Wholesale.Application;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Spec 2026-11-18: el adaptador que manda el aviso de la invitación por Mensajería. Llena las tres variables propias de la
/// plantilla (las cuatro fijas las pone Mensajería), usa el id del mensaje del outbox como <c>clientId</c> (D-5) y traduce el
/// resultado a los códigos que se guardan en la invitación.
/// </summary>
public sealed class WholesaleInvitationNoticeSenderTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ConnectionId = Guid.CreateVersion7();
    private static readonly ApprovedTemplate Template =
        new("qep_mayorista_invitacion_v2", "es", "UTILITY", "NAMED", "[]");

    private readonly StubReadiness _readiness = new();
    private readonly StubOutbound _outbound = new();
    private readonly StubTenants _tenants = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public WholesaleInvitationNoticeSenderTests()
    {
        _readiness.Approved[(TenantId, ConnectionId)] = Template;
        _tenants.Names[TenantId] = "Mayorista\n  Norte SAS";
    }

    [Fact]
    public async Task TheNoticeFillsContactWholesalerAndEmail()
    {
        var notice = Notice() with { ContactName = " Ana\tPérez " };

        var result = await Sender().SendAsync(notice, Ct);

        Assert.IsType<WholesaleNoticeSendResult.Sent>(result);
        var send = Assert.Single(_outbound.Sends);
        Assert.Equal(["correo", "mayorista", "nombre_contacto"], send.Values.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(("Ana Pérez", "Mayorista Norte SAS", "ana@distri.co"),
            (send.Values["nombre_contacto"], send.Values["mayorista"], send.Values["correo"]));
        Assert.Equal(
            (TenantId, ConnectionId, notice.SentByMemberId, "573001234567", "Ana Pérez", Template),
            (send.TenantId, send.ConnectionId, send.SentByMemberId, send.WaId, send.RecipientName, send.Template));
    }

    // El nombre del mayorista se lee al entregar, como el correo: un tenant renombrado sale con el nombre vigente.
    [Fact]
    public async Task TheWholesalerIsTheCurrentTenantName()
    {
        _tenants.Names[TenantId] = "Mayorista Renombrado";

        await Sender().SendAsync(Notice(), Ct);

        Assert.Equal("Mayorista Renombrado", Assert.Single(_outbound.Sends).Values["mayorista"]);
    }

    [Fact]
    public async Task ATemplateThatIsNotApprovedFailsWithoutSending()
    {
        _readiness.Approved.Clear();

        var result = await Sender().SendAsync(Notice(), Ct);

        Assert.Equal("template_not_approved", Assert.IsType<WholesaleNoticeSendResult.Failed>(result).Code);
        Assert.Empty(_outbound.Sends);
    }

    // Meta rechaza una variable vacía: sin nombre de tenant no se llama a Mensajería.
    [Fact]
    public async Task ATenantWithoutANameFailsAsRejectedWithoutSending()
    {
        _tenants.Names.Clear();

        var result = await Sender().SendAsync(Notice(), Ct);

        Assert.Equal("send_failed:messaging.message.rejected", Assert.IsType<WholesaleNoticeSendResult.Failed>(result).Code);
        Assert.Empty(_outbound.Sends);
    }

    [Fact]
    public async Task ConnectionUnavailableAndFailedMapToTheFailureCodes()
    {
        Assert.Equal("connection_unavailable:paused",
            await FailureFor(new WholesaleInvitationNoticeSendResult.ConnectionUnavailable("paused")));
        Assert.Equal("connection_unavailable:unknown",
            await FailureFor(new WholesaleInvitationNoticeSendResult.ConnectionUnavailable(null)));
        Assert.Equal("send_failed:messaging.message.rejected",
            await FailureFor(new WholesaleInvitationNoticeSendResult.Failed("messaging.message.rejected")));
    }

    // D-5: el reclamo de Mensajería es idempotente por clientId; el id del mensaje del outbox no cambia entre reintentos.
    [Fact]
    public async Task TheClientIdIsTheOutboxMessageId()
    {
        var notice = Notice();

        await Sender().SendAsync(notice, Ct);
        await Sender().SendAsync(notice, Ct);

        Assert.All(_outbound.Sends, send => Assert.Equal(notice.MessageId, send.ClientId));
        Assert.Equal(2, _outbound.Sends.Count);
    }

    // Spec 2026-11-19 §2: el link llega hasta acá, pero Mensajería guarda el texto armado de la plantilla tal cual
    // (PlainTemplateOutbound no enmascara), así que el token no se le pasa: ni como variable ni dentro de otra. Cuando Mensajería
    // pueda enmascararlo, esta prueba cambia con la plantilla nueva.
    [Fact]
    public async Task TheStoredMessageNeverContainsTheToken()
    {
        const string Token = "Zx0pQ9mW3rT7yU1iO5aS8dF2gH6jK4lZ0xC3vB7nM1q";
        var notice = Notice() with { Link = $"https://qep.qcode.co/wholesale-invitations/{Token}" };

        var result = await Sender().SendAsync(notice, Ct);

        Assert.IsType<WholesaleNoticeSendResult.Sent>(result);
        var send = Assert.Single(_outbound.Sends);
        Assert.Equal(["correo", "mayorista", "nombre_contacto"], send.Values.Keys.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(send.Values.Values, value => value.Contains(Token, StringComparison.Ordinal));
        Assert.DoesNotContain(Token, send.ToString(), StringComparison.Ordinal);
    }

    private async Task<string> FailureFor(WholesaleInvitationNoticeSendResult outcome)
    {
        _outbound.Next = outcome;
        var result = await Sender().SendAsync(Notice(), Ct);
        return Assert.IsType<WholesaleNoticeSendResult.Failed>(result).Code;
    }

    private WholesaleInvitationNoticeSender Sender() => new(_outbound, _readiness, _tenants);

    private static WholesaleInvitationNotice Notice() => new(
        Guid.CreateVersion7(), TenantId, ConnectionId, Guid.CreateVersion7(), "+573001234567", "Ana Pérez", "ana@distri.co");

    private sealed class StubReadiness : IWholesaleInvitationNoticeReadiness
    {
        public Dictionary<(Guid TenantId, Guid ConnectionId), ApprovedTemplate> Approved { get; } = [];

        public Task<ApprovedTemplate?> FindApprovedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
            Task.FromResult(Approved.GetValueOrDefault((tenantId, connectionId)));
    }

    private sealed class StubOutbound : IWholesaleInvitationNoticeOutbound
    {
        public List<WholesaleInvitationNoticeSend> Sends { get; } = [];

        public WholesaleInvitationNoticeSendResult Next { get; set; } =
            new WholesaleInvitationNoticeSendResult.Sent(Guid.CreateVersion7(), Guid.CreateVersion7());

        public Task<WholesaleInvitationNoticeSendResult> SendAsync(WholesaleInvitationNoticeSend send, CancellationToken cancellationToken)
        {
            Sends.Add(send);
            return Task.FromResult(Next);
        }
    }

    private sealed class StubTenants : ITenantDirectory
    {
        public Dictionary<Guid, string> Names { get; } = [];

        public Task<string?> GetDisplayNameAsync(TenantId tenantId, CancellationToken cancellationToken) =>
            Task.FromResult(Names.GetValueOrDefault(tenantId.Value));

        public Task<string?> GetSlugAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string?> GetTimeZoneAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string?> GetDefaultCurrencyAsync(TenantId tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Guid?> GetLogoFileIdAsync(TenantId tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TenantStatus?> GetStatusAsync(TenantId tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TenantRegionalSettings?> GetRegionalSettingsAsync(TenantId tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
