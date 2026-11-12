using BuildingBlocks.Application;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-11-09, prueba 5b y D8b: descartar un programado <c>Failed</c> lo cierra y, en la misma transacción, quema el
/// reintento del mensaje <c>Failed</c> que dejó (<c>CommitDiscardAsync</c>). Lo que ese commit hace sobre la fila está en las pruebas
/// de integración de persistencia.</summary>
public sealed class DiscardScheduledMessageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ScheduledMessage Failed(ScheduledMessageTestBed test, ConversationRow conversation, bool media = false)
    {
        var message = media
            ? ScheduledMessage.CreateMedia(
                Guid.CreateVersion7(), test.Bed.TenantId, conversation.Id, Guid.CreateVersion7(), MessageKind.Image, false, null,
                new ScheduledMedia("messaging/scheduled/y.png", "image/png", "a.png", 10, null), test.Bed.Now.AddHours(2), test.Bed.MemberId, test.Bed.Now)
            : ScheduledMessage.CreateText(
                Guid.CreateVersion7(), test.Bed.TenantId, conversation.Id, Guid.CreateVersion7(), "Hola", test.Bed.Now.AddHours(2), test.Bed.MemberId, test.Bed.Now);
        message.MarkFailed(MessagingErrorCodes.ScheduledUnknownOutcome, test.Bed.Now);
        test.Scheduled.Rows.Add(message);
        return message;
    }

    [Fact]
    public async Task DiscardCancelsTheRowAndBurnsTheFailedMessage()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1, assignedTo: Guid.CreateVersion7());
        var row = Failed(test, conversation, media: true);

        Assert.True(await test.DiscardHandler().HandleAsync(new DiscardScheduledMessageCommand(test.Bed.TenantId, conversation.Id, row.Id, row.Version), Ct));

        Assert.Equal((ScheduledMessageStatus.Cancelled, test.Bed.MemberId), (row.Status, row.CancelledByMemberId));
        Assert.Equal([row.Id], test.Scheduled.DiscardCommits);
        Assert.Empty(test.Scheduled.Commits);
        var audit = Assert.Single(test.Audit.ScheduledMessages);
        Assert.Equal((MessagingAuditActions.ScheduledMessageDiscarded, row.Id, test.Bed.SubjectId), (audit.Action, audit.ScheduledMessageId, audit.ActorId));
        Assert.Equal(["messaging/scheduled/y.png"], test.Bed.Store.Deleted);
        Assert.Single(test.Bed.Notifier.Events);
    }

    [Fact]
    public async Task DiscardWithoutAMessageRowOnlyCancels()
    {
        // Falló antes del reclamo: no hay fila de mensaje. El candado lo resuelve; el handler siempre pide el commit de descarte.
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1);
        var row = Failed(test, conversation);

        Assert.True(await test.DiscardHandler().HandleAsync(new DiscardScheduledMessageCommand(test.Bed.TenantId, conversation.Id, row.Id, row.Version), Ct));

        Assert.Equal(ScheduledMessageStatus.Cancelled, row.Status);
        Assert.Empty(test.Bed.Store.Deleted);
    }

    [Fact]
    public async Task DiscardingARowThatIsNotFailedIsNotRetriable()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1);
        var row = ScheduledMessage.CreateText(
            Guid.CreateVersion7(), test.Bed.TenantId, conversation.Id, Guid.CreateVersion7(), "Hola", test.Bed.Now.AddHours(2), test.Bed.MemberId, test.Bed.Now);
        test.Scheduled.Rows.Add(row);

        var error = await Assert.ThrowsAsync<ResourceConflictException>(() => test.DiscardHandler().HandleAsync(
            new DiscardScheduledMessageCommand(test.Bed.TenantId, conversation.Id, row.Id, row.Version), Ct));

        Assert.Equal("messaging.scheduled.not_retriable", error.Code);
        Assert.Empty(test.Scheduled.DiscardCommits);
    }

    [Fact]
    public async Task ADiscardWithAStaleVersionIs412()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1);
        var row = Failed(test, conversation);

        await Assert.ThrowsAsync<RequestConcurrencyException>(() => test.DiscardHandler().HandleAsync(
            new DiscardScheduledMessageCommand(test.Bed.TenantId, conversation.Id, row.Id, 99), Ct));

        Assert.Equal(ScheduledMessageStatus.Failed, row.Status);
    }
}
