using Bootstrapper;
using Modules.Messaging.Application;
using Modules.Notifications.Application;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Task-notices spec 2026-11-22 §5.6: the adapter that sends the approved <c>TaskNotice</c> template through Messaging. It fills only the
/// ticket number and the contact name (Messaging fills the fixed ones), never the subject, and maps Messaging's results to the codes the
/// escalation stores in <c>channel_attempts</c>.
/// </summary>
public sealed class TaskNoticeWhatsAppSenderTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ConnectionId = Guid.CreateVersion7();
    private static readonly ApprovedTemplate Template = new("qep_tarea_asignada_v2", "es", "UTILITY", "NAMED", "[]");

    private readonly StubReadiness _readiness = new();
    private readonly StubOutbound _outbound = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public TaskNoticeWhatsAppSenderTests() => _readiness.Approved[(TenantId, ConnectionId)] = Template;

    [Fact]
    public async Task WithoutAnApprovedTemplateFailsWithTemplateNotApproved()
    {
        _readiness.Approved.Clear();

        var result = await Sender().SendAsync(Send(), Ct);

        Assert.Equal("template_not_approved", Assert.IsType<TaskNoticeWhatsAppResult.Failed>(result).Code);
        Assert.Empty(_outbound.Sends);
    }

    [Fact]
    public async Task SendsOnlyTheNumberAndTheContactName()
    {
        var send = Send() with { RecipientName = " Ana\tPérez\n" };

        var result = await Sender().SendAsync(send, Ct);

        Assert.IsType<TaskNoticeWhatsAppResult.Sent>(result);
        var sent = Assert.Single(_outbound.Sends);
        Assert.Equal(["nombre_contacto", "numero"], sent.Values.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("SOP-000127", sent.Values["numero"]);
        Assert.Equal("Ana Pérez", sent.Values["nombre_contacto"]);
        Assert.Equal((TenantId, ConnectionId, send.SentByMemberId, send.ClientId, Template), (sent.TenantId, sent.ConnectionId, sent.SentByMemberId, sent.ClientId, sent.Template));
    }

    [Fact]
    public async Task TheWaIdIsDigitsOnly()
    {
        await Sender().SendAsync(Send(), Ct);

        Assert.Equal("573001234567", Assert.Single(_outbound.Sends).WaId);
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

    private TaskNoticeWhatsAppSender Sender() => new(_readiness, _outbound);

    private static TaskNoticeWhatsAppSend Send() =>
        new(TenantId, ConnectionId, Guid.CreateVersion7(), Guid.CreateVersion7(), "+573001234567", "Ana Pérez", "SOP-000127");

    private sealed class StubReadiness : ITaskNoticeTemplateReadiness
    {
        public Dictionary<(Guid TenantId, Guid ConnectionId), ApprovedTemplate> Approved { get; } = [];

        public Task<ApprovedTemplate?> FindApprovedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
            Task.FromResult(Approved.GetValueOrDefault((tenantId, connectionId)));

        public Task<string?> FindStatusAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubOutbound : ITaskNoticeOutbound
    {
        public List<TaskNoticeSend> Sends { get; } = [];

        public TaskNoticeSendResult Next { get; set; } = new TaskNoticeSendResult.Sent(Guid.CreateVersion7(), Guid.CreateVersion7());

        public Task<TaskNoticeSendResult> SendAsync(TaskNoticeSend send, CancellationToken cancellationToken)
        {
            Sends.Add(send);
            return Task.FromResult(Next);
        }
    }
}
