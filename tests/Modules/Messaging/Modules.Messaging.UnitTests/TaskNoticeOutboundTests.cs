using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;

namespace Modules.Messaging.UnitTests;

/// <summary>Support task notices: sending the TaskNotice template goes through the plain-template core, with no customer and no positional
/// order. What is proven here: which values travel, who is the author, idempotency by <c>ClientId</c> and the paused-connection result.</summary>
public sealed class TaskNoticeOutboundTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ApprovedTemplate Template = new(
        "qep_tarea_asignada", "es", "UTILITY", "NAMED",
        TaskNoticeTemplateDefinition.Payload("qep_tarea_asignada", TaskNoticeTemplateDefinition.BaseBody)["components"]!.ToJsonString());

    private static TaskNoticeOutbound Outbound(MessagingTestBed bed) =>
        new(bed.Conversations, bed.Repository, bed.Outbound, bed.Meta, bed.Connections, bed.Notifier, bed.Receipts, new FakeClock(bed.Now),
            bed.Opener, new TemplateSenderValues(new FakeMemberNames(bed.MemberId), new Tenants(), new NoClock()));

    private static TaskNoticeSend Send(MessagingTestBed bed) =>
        new(bed.TenantId, bed.ConnectionId, bed.MemberId, bed.ClientId, "573001234567", "Laura Gómez", Template,
            new Dictionary<string, string> { [TaskNoticeTemplateDefinition.NumberKey] = "SOP-0042" });

    [Fact]
    public async Task SendsTheNamedValuesWithTheSenderAsAuthor()
    {
        var bed = new MessagingTestBed();

        var result = await Outbound(bed).SendAsync(Send(bed), Ct);

        Assert.IsType<TaskNoticeSendResult.Sent>(result);
        var claim = Assert.Single(bed.Outbound.Claims);
        Assert.Equal((bed.ClientId, bed.MemberId, MessageKind.Template), (claim.Draft.ClientId, claim.Draft.SentByMemberId, claim.Draft.Kind));
        var sent = Assert.Single(bed.Meta.TemplateSends);
        Assert.True(sent.Template.Named);
        Assert.Equal(
            new Dictionary<string, string> { ["asesor"] = "Andrés", ["numero"] = "SOP-0042" },
            sent.Template.Parameters.ToDictionary(parameter => parameter.Key, parameter => parameter.Value));
        // No customer: the assignee is a member, not a customer of the tenant.
        Assert.Null(Assert.Single(bed.Opener.PhoneCalls).CustomerId);
    }

    [Fact]
    public async Task TheSameClientIdTwiceIsOneMessage()
    {
        var bed = new MessagingTestBed();
        var first = Assert.IsType<TaskNoticeSendResult.Sent>(await Outbound(bed).SendAsync(Send(bed), Ct));
        bed.Outbound.Existing = new ExistingOutbound(first.MessageId, MessageStatus.Sent, bed.Now, bed.MemberId, "text");

        var second = Assert.IsType<TaskNoticeSendResult.Sent>(await Outbound(bed).SendAsync(Send(bed), Ct));

        Assert.Equal(first.MessageId, second.MessageId);
        Assert.Single(bed.Meta.TemplateSends);
    }

    [Fact]
    public async Task APausedConnectionIsConnectionUnavailable()
    {
        var bed = new MessagingTestBed { Connections = { Sender = null } };

        var result = await Outbound(bed).SendAsync(Send(bed), Ct);

        Assert.Equal(new TaskNoticeSendResult.ConnectionUnavailable("not_found"), result);
        Assert.Empty(bed.Meta.TemplateSends);
    }

    private sealed class Tenants : IMessagingTenantNames
    {
        public Task<string?> FindAsync(Guid tenantId, CancellationToken cancellationToken) => Task.FromResult<string?>("Mi Empresa");
    }

    private sealed class NoClock : ITenantClock
    {
        public Task<TenantCalendar> GetAsync(Guid tenantId, CancellationToken cancellationToken) => throw new InvalidOperationException("Not expected.");
    }
}
