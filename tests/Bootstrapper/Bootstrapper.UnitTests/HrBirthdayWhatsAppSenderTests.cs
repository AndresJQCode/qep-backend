using Bootstrapper;
using Modules.Messaging.Application;
using Modules.Notifications.Application;

namespace Bootstrapper.UnitTests;

/// <summary>
/// HR birthday notices, spec 2026-11-26 §5.3: the adapter that sends the approved <c>HrBirthdayNotice</c> template through Messaging. It fills the
/// four variables of that template (nothing else: the fixed advisor, company and greeting are not in its body), one line each, and maps Messaging's
/// results to the codes the delivery row stores.
/// </summary>
public sealed class HrBirthdayWhatsAppSenderTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ConnectionId = Guid.CreateVersion7();
    private static readonly ApprovedTemplate Template = new("qep_cumpleanos_equipo_v2", "es", "UTILITY", "NAMED", "[]");

    private readonly StubReadiness _readiness = new();
    private readonly StubOutbound _outbound = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public HrBirthdayWhatsAppSenderTests() => _readiness.Approved[(TenantId, ConnectionId)] = Template;

    [Fact]
    public async Task NotApprovedTemplateFails()
    {
        _readiness.Approved.Clear();

        var result = await Sender().SendAsync(Send(), Ct);

        Assert.Equal("template_not_approved", Assert.IsType<TaskNoticeWhatsAppResult.Failed>(result).Code);
        Assert.Empty(_outbound.Sends);
    }

    [Fact]
    public async Task FillsTheFourVariables()
    {
        var send = Send() with { RecipientName = " Laura\tGómez\n", Today = "Ana\nPérez", Upcoming = "Luis  Gómez", UpcomingDate = "viernes 4 de diciembre" };

        var result = await Sender().SendAsync(send, Ct);

        Assert.IsType<TaskNoticeWhatsAppResult.Sent>(result);
        var sent = Assert.Single(_outbound.Sends);
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["nombre_contacto"] = "Laura Gómez",
                ["hoy"] = "Ana Pérez",
                ["fecha"] = "viernes 4 de diciembre",
                ["proximos"] = "Luis Gómez",
            },
            sent.Values);
        Assert.Equal("573001234567", sent.WaId);
        Assert.Equal((TenantId, ConnectionId, send.SentByMemberId, send.ClientId, Template), (sent.TenantId, sent.ConnectionId, sent.SentByMemberId, sent.ClientId, sent.Template));
    }

    [Theory]
    [InlineData("paused", "connection_unavailable:paused")]
    [InlineData(null, "connection_unavailable:unknown")]
    public async Task MapsConnectionUnavailable(string? reason, string expected)
    {
        _outbound.Next = new TaskNoticeSendResult.ConnectionUnavailable(reason);

        var result = await Sender().SendAsync(Send(), Ct);

        Assert.Equal(expected, Assert.IsType<TaskNoticeWhatsAppResult.Failed>(result).Code);
    }

    [Fact]
    public async Task MapsFailed()
    {
        _outbound.Next = new TaskNoticeSendResult.Failed("messaging.message.rejected");

        var result = await Sender().SendAsync(Send(), Ct);

        Assert.Equal("send_failed:messaging.message.rejected", Assert.IsType<TaskNoticeWhatsAppResult.Failed>(result).Code);
    }

    private HrBirthdayWhatsAppSender Sender() => new(_readiness, _outbound);

    private static HrBirthdayWhatsAppSend Send() =>
        new(TenantId, ConnectionId, Guid.CreateVersion7(), Guid.CreateVersion7(), "+573001234567", "Laura Gómez", "Ana Pérez", "viernes 4 de diciembre", "Luis Gómez");

    private sealed class StubReadiness : IHrBirthdayNoticeTemplateReadiness
    {
        public Dictionary<(Guid TenantId, Guid ConnectionId), ApprovedTemplate> Approved { get; } = [];

        public Task<ApprovedTemplate?> FindApprovedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
            Task.FromResult(Approved.GetValueOrDefault((tenantId, connectionId)));

        public Task<string?> FindStatusAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubOutbound : IHrBirthdayNoticeOutbound
    {
        public List<HrBirthdayNoticeSend> Sends { get; } = [];

        public TaskNoticeSendResult Next { get; set; } = new TaskNoticeSendResult.Sent(Guid.CreateVersion7(), Guid.CreateVersion7());

        public Task<TaskNoticeSendResult> SendAsync(HrBirthdayNoticeSend send, CancellationToken cancellationToken)
        {
            Sends.Add(send);
            return Task.FromResult(Next);
        }
    }
}
