using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;

namespace Modules.Messaging.UnitTests;

/// <summary>HR birthday notices: the digest goes through the plain-template core, with no customer. Proven here: only the four variables of the
/// template travel (the fixed advisor, company and greeting are absent from the body, so nothing else is looked up or sent), the author is the
/// given member and the same <c>ClientId</c> twice is one message.</summary>
public sealed class HrBirthdayNoticeOutboundTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ApprovedTemplate Template = new(
        "qep_cumpleanos_equipo", "es", "UTILITY", "NAMED",
        HrBirthdayNoticeTemplateDefinition.Payload("qep_cumpleanos_equipo", HrBirthdayNoticeTemplateDefinition.BaseBody)["components"]!.ToJsonString());

    private static HrBirthdayNoticeOutbound Outbound(MessagingTestBed bed) =>
        new(bed.Conversations, bed.Repository, bed.Outbound, bed.Meta, bed.Connections, bed.Notifier, bed.Receipts, new FakeClock(bed.Now),
            bed.Opener, new TemplateSenderValues(new FakeMemberNames(bed.MemberId), new FailingTenants(), new NoClock()));

    private static HrBirthdayNoticeSend Send(MessagingTestBed bed) =>
        new(bed.TenantId, bed.ConnectionId, bed.MemberId, bed.ClientId, "573001234567", "Laura Gómez", Template,
            new Dictionary<string, string>
            {
                [SystemTemplateVariables.ContactName] = "Laura Gómez",
                [HrBirthdayNoticeTemplateDefinition.TodayKey] = "Ana Pérez y Luis Rojas",
                [HrBirthdayNoticeTemplateDefinition.DateKey] = "viernes 4 de diciembre",
                [HrBirthdayNoticeTemplateDefinition.UpcomingKey] = "Marta Díaz",
            });

    [Fact]
    public async Task SendsTheApprovedTemplateWithTheGivenValues()
    {
        var bed = new MessagingTestBed();

        var result = await Outbound(bed).SendAsync(Send(bed), Ct);

        Assert.IsType<TaskNoticeSendResult.Sent>(result);
        var claim = Assert.Single(bed.Outbound.Claims);
        Assert.Equal((bed.ClientId, bed.MemberId, MessageKind.Template), (claim.Draft.ClientId, claim.Draft.SentByMemberId, claim.Draft.Kind));
        var sent = Assert.Single(bed.Meta.TemplateSends);
        Assert.True(sent.Template.Named);
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["nombre_contacto"] = "Laura Gómez",
                ["hoy"] = "Ana Pérez y Luis Rojas",
                ["fecha"] = "viernes 4 de diciembre",
                ["proximos"] = "Marta Díaz",
            },
            sent.Template.Parameters.ToDictionary(parameter => parameter.Key, parameter => parameter.Value));
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

    // The body carries neither the advisor nor the company: they are never looked up.
    private sealed class FailingTenants : IMessagingTenantNames
    {
        public Task<string?> FindAsync(Guid tenantId, CancellationToken cancellationToken) => throw new InvalidOperationException("Not expected.");
    }

    private sealed class NoClock : ITenantClock
    {
        public Task<TenantCalendar> GetAsync(Guid tenantId, CancellationToken cancellationToken) => throw new InvalidOperationException("Not expected.");
    }
}
