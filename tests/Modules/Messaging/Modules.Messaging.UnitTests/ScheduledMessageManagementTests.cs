using BuildingBlocks.Application;
using FluentValidation;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-11-09, pruebas 4 y 5b: editar, cancelar, «Enviar ya» y reintentar un programado. Sólo cuenta el permiso de
/// responder, no ser el asignado; el estado (409) se mira antes que la versión (412).</summary>
public sealed class ScheduledMessageManagementTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ScheduledMessage Row(
        ScheduledMessageTestBed test, ConversationRow conversation, ScheduledMessageStatus status = ScheduledMessageStatus.Scheduled,
        DateTimeOffset? at = null, string? caption = null, bool voiceNote = false, MessageKind kind = MessageKind.Text)
    {
        var author = Guid.CreateVersion7();
        var scheduledAt = at ?? test.Bed.Now.AddHours(2);
        var message = kind == MessageKind.Text
            ? ScheduledMessage.CreateText(Guid.CreateVersion7(), test.Bed.TenantId, conversation.Id, Guid.CreateVersion7(), "Hola", scheduledAt, author, test.Bed.Now)
            : ScheduledMessage.CreateMedia(
                Guid.CreateVersion7(), test.Bed.TenantId, conversation.Id, Guid.CreateVersion7(), kind, voiceNote, caption,
                new ScheduledMedia("messaging/scheduled/x.png", "image/png", "a.png", 10, null), scheduledAt, author, test.Bed.Now);
        switch (status)
        {
            case ScheduledMessageStatus.Sending:
                message.MarkSending(test.Bed.Now);
                break;
            case ScheduledMessageStatus.Failed:
                message.MarkFailed(MessagingErrorCodes.ScheduledUnknownOutcome, test.Bed.Now);
                break;
        }

        test.Scheduled.Rows.Add(message);
        return message;
    }

    private static UpdateScheduledMessageCommand Put(
        ScheduledMessageTestBed test, ScheduledMessage row, long? version = null, DateTimeOffset? at = null, string? text = null, string? caption = null) =>
        new(test.Bed.TenantId, row.ConversationId, row.Id, version ?? row.Version, at, text, caption);

    [Fact]
    public async Task AnotherMemberWithPermissionCanEditCancelAndSendNowWithoutBeingTheAssignee()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1, assignedTo: Guid.CreateVersion7());
        var row = Row(test, conversation);

        var edited = await test.UpdateHandler().HandleAsync(Put(test, row, text: "Nuevo"), Ct);
        Assert.Equal(("Nuevo", 2L), (edited.Text, edited.Version));
        Assert.Equal(test.Bed.MemberId, row.UpdatedByMemberId);

        var sent = await test.SendNowHandler().HandleAsync(
            new SendScheduledMessageNowCommand(test.Bed.TenantId, conversation.Id, row.Id, row.Version), Ct);
        Assert.Equal(test.Bed.Now, sent.ScheduledAt);

        Assert.True(await test.CancelHandler().HandleAsync(
            new CancelScheduledMessageCommand(test.Bed.TenantId, conversation.Id, row.Id, row.Version), Ct));
        Assert.Equal(ScheduledMessageStatus.Cancelled, row.Status);
    }

    [Fact]
    public async Task WithoutThePermissionNothingIsLocked()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1);
        var row = Row(test, conversation);

        await Assert.ThrowsAsync<RequestForbiddenException>(() => test.CancelHandler(canManage: false).HandleAsync(
            new CancelScheduledMessageCommand(test.Bed.TenantId, conversation.Id, row.Id, row.Version), Ct));
        Assert.Equal(0, test.Scheduled.Locks);
    }

    [Fact]
    public async Task AStaleExpectedVersionIs412()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1);
        var row = Row(test, conversation);

        var error = await Assert.ThrowsAsync<RequestConcurrencyException>(() => test.UpdateHandler().HandleAsync(Put(test, row, version: 7, text: "x"), Ct));

        Assert.Equal("concurrency.conflict", error.Code);
        Assert.Empty(test.Scheduled.Commits);
        Assert.Equal("Hola", row.Text);
    }

    [Fact]
    public async Task AMissingExpectedVersionIsAValidationErrorOnExpectedVersion()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1);
        var row = Row(test, conversation);

        var error = await Assert.ThrowsAsync<ValidationException>(() => test.CancelHandler().HandleAsync(
            new CancelScheduledMessageCommand(test.Bed.TenantId, conversation.Id, row.Id, null), Ct));

        Assert.Equal("expectedVersion", Assert.Single(error.Errors).PropertyName);
    }

    [Fact]
    public async Task EditingASendingRowIsNotEditable()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1);
        var row = Row(test, conversation, ScheduledMessageStatus.Sending);

        // Con una versión vieja también: el estado se mira antes que la versión.
        var error = await Assert.ThrowsAsync<ResourceConflictException>(() => test.UpdateHandler().HandleAsync(Put(test, row, version: 99, text: "x"), Ct));

        Assert.Equal("messaging.scheduled.not_editable", error.Code);
    }

    [Fact]
    public async Task CancellingOrSendingNowARowThatIsNotScheduledIsNotEditable()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1);
        var row = Row(test, conversation, ScheduledMessageStatus.Failed);

        var cancel = await Assert.ThrowsAsync<ResourceConflictException>(() => test.CancelHandler().HandleAsync(
            new CancelScheduledMessageCommand(test.Bed.TenantId, conversation.Id, row.Id, row.Version), Ct));
        var now = await Assert.ThrowsAsync<ResourceConflictException>(() => test.SendNowHandler().HandleAsync(
            new SendScheduledMessageNowCommand(test.Bed.TenantId, conversation.Id, row.Id, row.Version), Ct));

        Assert.Equal(("messaging.scheduled.not_editable", "messaging.scheduled.not_editable"), (cancel.Code, now.Code));
    }

    [Fact]
    public async Task RetryingAScheduledRowIsNotRetriable()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1);
        var row = Row(test, conversation);

        var error = await Assert.ThrowsAsync<ResourceConflictException>(() => test.RetryHandler().HandleAsync(
            new RetryScheduledMessageCommand(test.Bed.TenantId, conversation.Id, row.Id, row.Version), Ct));

        Assert.Equal("messaging.scheduled.not_retriable", error.Code);
    }

    [Fact]
    public async Task AnOmittedOrNullFieldIsKept()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1);
        var row = Row(test, conversation, kind: MessageKind.Image, caption: "Pie");

        var dto = await test.UpdateHandler().HandleAsync(Put(test, row, text: null, caption: null), Ct);

        Assert.Equal(("Pie", 1L), (dto.Caption, dto.Version));
        Assert.Empty(test.Scheduled.Commits);
    }

    [Fact]
    public async Task APutWithoutScheduledAtKeepsTheStoredTime()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1);
        // Guardada a 30 s: ya no se aceptaría como hora nueva (too_soon), pero un PUT que no la manda no la toca.
        var stored = test.Bed.Now.AddSeconds(30);
        var row = Row(test, conversation, at: stored);

        var dto = await test.UpdateHandler().HandleAsync(Put(test, row, at: null, text: "Nuevo"), Ct);

        Assert.Equal((stored, "Nuevo"), (dto.ScheduledAt, dto.Text));
        Assert.Equal(["text"], test.Audit.ScheduledMessages.Single().ChangedFields);
    }

    [Fact]
    public async Task ASentScheduledAtEqualToTheStoredOneIsNotRevalidated()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1);
        var stored = test.Bed.Now.AddSeconds(30);
        var row = Row(test, conversation, at: stored);

        var dto = await test.UpdateHandler().HandleAsync(Put(test, row, at: stored, text: "Nuevo"), Ct);

        Assert.Equal("Nuevo", dto.Text);
    }

    [Fact]
    public async Task ADifferentScheduledAtIsCheckedForTooSoonAndWindow()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1);
        var row = Row(test, conversation);

        var soon = await Assert.ThrowsAsync<MessagingDomainException>(() => test.UpdateHandler().HandleAsync(Put(test, row, at: test.Bed.Now.AddSeconds(10)), Ct));
        var late = await Assert.ThrowsAsync<MessagingDomainException>(() => test.UpdateHandler().HandleAsync(Put(test, row, at: test.Bed.Now.AddHours(30)), Ct));

        Assert.Equal(("messaging.scheduled.too_soon", "messaging.scheduled.outside_window"), (soon.Code, late.Code));
        Assert.Empty(test.Scheduled.Commits);
    }

    [Fact]
    public async Task AnEmptyCaptionClearsIt()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1);
        var row = Row(test, conversation, kind: MessageKind.Image, caption: "Pie");

        var dto = await test.UpdateHandler().HandleAsync(Put(test, row, caption: ""), Ct);

        Assert.Null(dto.Caption);
        Assert.Equal(["caption"], test.Audit.ScheduledMessages.Single().ChangedFields);
    }

    [Fact]
    public async Task AnEmptyTextIsAValidationErrorOnText()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1);
        var row = Row(test, conversation);

        var error = await Assert.ThrowsAsync<ValidationException>(() => test.UpdateHandler().HandleAsync(Put(test, row, text: "  "), Ct));

        var failure = Assert.Single(error.Errors);
        Assert.Equal(("text", "Escribe un mensaje de hasta 4096 caracteres."), (failure.PropertyName, failure.ErrorMessage));
        Assert.Equal("Hola", row.Text);
    }

    [Fact]
    public async Task ATextOnAMediaRowIsAValidationErrorOnText()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1);
        var row = Row(test, conversation, kind: MessageKind.Image);

        var error = await Assert.ThrowsAsync<ValidationException>(() => test.UpdateHandler().HandleAsync(Put(test, row, text: "hola"), Ct));

        Assert.Equal("text", Assert.Single(error.Errors).PropertyName);
    }

    [Fact]
    public async Task ACaptionOnATextRowIsAValidationErrorOnCaption()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1);
        var row = Row(test, conversation);

        var error = await Assert.ThrowsAsync<ValidationException>(() => test.UpdateHandler().HandleAsync(Put(test, row, caption: "pie"), Ct));

        Assert.Equal("caption", Assert.Single(error.Errors).PropertyName);
    }

    [Fact]
    public async Task ACaptionOnAVoiceNoteIsAValidationErrorOnCaption()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1);
        var row = Row(test, conversation, kind: MessageKind.Audio, voiceNote: true);

        var error = await Assert.ThrowsAsync<ValidationException>(() => test.UpdateHandler().HandleAsync(Put(test, row, caption: "pie"), Ct));

        Assert.Equal("caption", Assert.Single(error.Errors).PropertyName);
    }

    [Fact]
    public async Task TheTextLimitCountsTheLongestGreeting()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1);
        var row = Row(test, conversation);

        // 4088 + 8 = 4096 crudo (cabe), 4088 + 13 = 4101 ya resuelto (no cabe).
        var error = await Assert.ThrowsAsync<ValidationException>(() => test.UpdateHandler().HandleAsync(
            Put(test, row, text: new string('a', 4088) + "{saludo}"), Ct));

        Assert.Equal("text", Assert.Single(error.Errors).PropertyName);
    }

    [Fact]
    public async Task AnIdenticalPutDoesNotBumpTheVersionNorAudit()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1);
        var row = Row(test, conversation);

        var dto = await test.UpdateHandler().HandleAsync(Put(test, row, at: row.ScheduledAt, text: "Hola"), Ct);

        Assert.Equal(1L, dto.Version);
        Assert.Empty(test.Scheduled.Commits);
        Assert.Empty(test.Audit.ScheduledMessages);
        Assert.Empty(test.Bed.Notifier.Events);
    }

    [Fact]
    public async Task ChangedFieldsListOnlyWhatReallyChanged()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1);
        var row = Row(test, conversation, kind: MessageKind.Image, caption: "Pie");

        await test.UpdateHandler().HandleAsync(Put(test, row, at: row.ScheduledAt, caption: "Otro"), Ct);

        var audit = Assert.Single(test.Audit.ScheduledMessages);
        Assert.Equal(
            (test.Bed.TenantId, test.Bed.SubjectId, false, MessagingAuditActions.ScheduledMessageUpdated, row.Id),
            (audit.TenantId, audit.ActorId, audit.BySystem, audit.Action, audit.ScheduledMessageId));
        Assert.Equal(["caption"], audit.ChangedFields);
        Assert.Equal([row.Id], test.Scheduled.Commits);
        Assert.Single(test.Bed.Notifier.Events);
    }

    [Fact]
    public async Task AFailingNotifierDoesNotUndoTheEdit()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1);
        var row = Row(test, conversation);
        test.Bed.Notifier.Fail = new InvalidOperationException("hub down");

        var dto = await test.UpdateHandler().HandleAsync(Put(test, row, text: "Nuevo"), Ct);

        Assert.Equal("Nuevo", dto.Text);
        Assert.Equal([row.Id], test.Scheduled.Commits);
    }

    [Fact]
    public async Task CancelAuditsAndDeletesTheMediaObject()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1);
        var row = Row(test, conversation, kind: MessageKind.Image);

        await test.CancelHandler().HandleAsync(new CancelScheduledMessageCommand(test.Bed.TenantId, conversation.Id, row.Id, row.Version), Ct);

        Assert.Equal(MessagingAuditActions.ScheduledMessageCancelled, test.Audit.ScheduledMessages.Single().Action);
        Assert.Equal(["messaging/scheduled/x.png"], test.Bed.Store.Deleted);
        Assert.Equal([row.Id], test.Scheduled.Commits);
        Assert.Single(test.Bed.Notifier.Events);
    }

    [Fact]
    public async Task ACancelWhoseMediaDeleteFailsStillSucceeds()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1);
        var row = Row(test, conversation, kind: MessageKind.Image);
        test.Bed.Store.DeleteFailure = new InvalidOperationException("r2");

        Assert.True(await test.CancelHandler().HandleAsync(new CancelScheduledMessageCommand(test.Bed.TenantId, conversation.Id, row.Id, row.Version), Ct));
    }

    [Fact]
    public async Task SendNowMovesScheduledAtToNowAndAudits()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1);
        var row = Row(test, conversation);

        var dto = await test.SendNowHandler().HandleAsync(new SendScheduledMessageNowCommand(test.Bed.TenantId, conversation.Id, row.Id, row.Version), Ct);

        Assert.Equal((test.Bed.Now, 2L, "Scheduled"), (dto.ScheduledAt, dto.Version, dto.Status));
        Assert.Equal(MessagingAuditActions.ScheduledMessageSendNow, test.Audit.ScheduledMessages.Single().Action);
        Assert.Equal([row.Id], test.Scheduled.Commits);
    }

    [Fact]
    public async Task RetryRequeuesAFailedRowAndAudits()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1);
        var row = Row(test, conversation, ScheduledMessageStatus.Failed);

        var dto = await test.RetryHandler().HandleAsync(new RetryScheduledMessageCommand(test.Bed.TenantId, conversation.Id, row.Id, row.Version), Ct);

        Assert.Equal(("Scheduled", test.Bed.Now, (string?)null), (dto.Status, dto.ScheduledAt, dto.FailureCode));
        Assert.Equal(MessagingAuditActions.ScheduledMessageRetried, test.Audit.ScheduledMessages.Single().Action);
        Assert.Equal([row.Id], test.Scheduled.Commits);
    }

    [Fact]
    public async Task RetryOutsideTheWindowIsOutsideWindow()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 24);
        var row = Row(test, conversation, ScheduledMessageStatus.Failed);

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => test.RetryHandler().HandleAsync(
            new RetryScheduledMessageCommand(test.Bed.TenantId, conversation.Id, row.Id, row.Version), Ct));

        Assert.Equal("messaging.scheduled.outside_window", error.Code);
        Assert.Equal(ScheduledMessageStatus.Failed, row.Status);
    }

    [Fact]
    public async Task RetryOnABlockedContactIsContactBlocked()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1, blocked: true);
        var row = Row(test, conversation, ScheduledMessageStatus.Failed);
        var version = row.Version;

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => test.RetryHandler().HandleAsync(
            new RetryScheduledMessageCommand(test.Bed.TenantId, conversation.Id, row.Id, row.Version), Ct));

        Assert.Equal("messaging.contact.blocked", error.Code);
        Assert.Equal((ScheduledMessageStatus.Failed, version), (row.Status, row.Version));
        Assert.Empty(test.Audit.ScheduledMessages);
        Assert.Empty(test.Scheduled.Commits);
    }

    [Fact]
    public async Task AnIdOfAnotherConversationIsNotFound()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1);
        var other = test.Bed.OpenConversation(lastInboundHoursAgo: 1, waId: "573009999999");
        var row = Row(test, other);

        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() => test.CancelHandler().HandleAsync(
            new CancelScheduledMessageCommand(test.Bed.TenantId, conversation.Id, row.Id, row.Version), Ct));

        Assert.Equal("messaging.scheduled.not_found", error.Code);
    }

    [Fact]
    public async Task AnUnknownConversationIsConversationNotFound()
    {
        var test = new ScheduledMessageTestBed();

        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() => test.CancelHandler().HandleAsync(
            new CancelScheduledMessageCommand(test.Bed.TenantId, Guid.CreateVersion7(), Guid.CreateVersion7(), 1), Ct));

        Assert.Equal("messaging.conversation.not_found", error.Code);
    }
}
