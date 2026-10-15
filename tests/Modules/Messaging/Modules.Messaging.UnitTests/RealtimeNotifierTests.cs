using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-14 (Mensajería en tiempo real), decisión 2: el canal avisa por ids después del commit.
/// Un saliente consolidado —Sent o Failed— avisa <c>conversationChanged</c>; un rollback (la credencial ya no
/// sirve) no avisa nada, porque no quedó fila que leer.</summary>
public sealed class RealtimeNotifierTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ASentMessageNotifiesTheConversationOnce()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);
        bed.Meta.NextSend = new SendResult(SendOutcome.Sent, "wamid.out", null, null);

        await bed.SendHandler().HandleAsync(new SendMessageCommand(bed.TenantId, conversation.Id, bed.ClientId, "Hola"), Ct);

        var notified = Assert.Single(bed.Notifier.Events);
        Assert.Equal((MessagingEventKinds.ConversationChanged, bed.TenantId, conversation.Id), (notified.Kind, notified.TenantId, notified.ConversationId));
    }

    [Fact]
    public async Task AFailedMessageAlsoNotifiesBecauseTheRowIsThere()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);
        bed.Meta.NextSend = new SendResult(SendOutcome.GraphError, null, 131026, "Message undeliverable");

        await Assert.ThrowsAsync<MessagingDomainException>(() => bed.SendHandler().HandleAsync(new SendMessageCommand(bed.TenantId, conversation.Id, bed.ClientId, "Hola"), Ct));

        Assert.Equal("committed-failed:131026", Assert.Single(bed.Outbound.Claims).Outcome);
        Assert.Equal(MessagingEventKinds.ConversationChanged, Assert.Single(bed.Notifier.Events).Kind);
    }

    [Fact]
    public async Task ARolledBackSendNotifiesNothing()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);
        bed.Meta.NextSend = new SendResult(SendOutcome.GraphError, null, MetaErrorCodes.TokenExpired, "Expired");

        await Assert.ThrowsAsync<MessagingDomainException>(() => bed.SendHandler().HandleAsync(new SendMessageCommand(bed.TenantId, conversation.Id, bed.ClientId, "Hola"), Ct));

        Assert.Equal("rolled-back", Assert.Single(bed.Outbound.Claims).Outcome);
        Assert.Empty(bed.Notifier.Events);
    }

    [Fact]
    public async Task ARepeatedClientIdDoesNotNotifyAgain()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);
        bed.Outbound.Committed = new ExistingOutbound(Guid.CreateVersion7(), MessageStatus.Sent, bed.Now, bed.MemberId, "Hola");

        await bed.SendHandler().HandleAsync(new SendMessageCommand(bed.TenantId, conversation.Id, bed.ClientId, "Hola"), Ct);

        Assert.Empty(bed.Notifier.Events);
    }
}
