using System.Text;
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-11-09, pruebas 3 y 3b: el orden de <see cref="CreateScheduledMessageHandler"/>. Primero el <c>clientId</c> ya guardado
/// (200, sin hora, admisión ni staging), después la hora, la admisión (que autoasigna) y el adjunto; el staging se consume sólo cuando la
/// fila quedó guardada.</summary>
public sealed class CreateScheduledMessageHandlerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7 contenido");
    private static readonly byte[] Exe = [0x4D, 0x5A, 0x90, 0x00, 3, 0, 0, 0, 4];

    private static CreateScheduledMessageCommand TextCommand(
        ScheduledMessageTestBed test, ConversationRow conversation, string text = "Hola {saludo}", DateTimeOffset? at = null) =>
        new(test.Bed.TenantId, conversation.Id, test.Bed.ClientId, at ?? test.Bed.Now.AddHours(2), text, null, null, null);

    private static CreateScheduledMessageCommand MediaCommand(
        ScheduledMessageTestBed test, ConversationRow conversation, Guid uploadId, string fileName, string? caption = null, DateTimeOffset? at = null) =>
        new(test.Bed.TenantId, conversation.Id, test.Bed.ClientId, at ?? test.Bed.Now.AddHours(2), null, uploadId, fileName, caption);

    private static ScheduledMessage RowIn(ScheduledMessageTestBed test, ConversationRow conversation, ScheduledMessageStatus status, Guid? clientId = null)
    {
        var message = ScheduledMessage.CreateText(
            Guid.CreateVersion7(), test.Bed.TenantId, conversation.Id, clientId ?? test.Bed.ClientId, "Hola", test.Bed.Now.AddHours(2), test.Bed.MemberId, test.Bed.Now);
        switch (status)
        {
            case ScheduledMessageStatus.Failed:
                message.MarkFailed(MessagingErrorCodes.ScheduledUnknownOutcome, test.Bed.Now);
                break;
            case ScheduledMessageStatus.Cancelled:
                message.Cancel(test.Bed.MemberId, test.Bed.Now);
                break;
            case ScheduledMessageStatus.Sent:
                message.MarkSending(test.Bed.Now);
                message.MarkSent(Guid.CreateVersion7(), test.Bed.Now);
                break;
        }

        test.Scheduled.Rows.Add(message);
        return message;
    }

    private static string ScheduledKey(ScheduledMessageTestBed test, Guid id, string extension) =>
        $"messaging/scheduled/{test.Bed.TenantId}/{id}{extension}";

    [Fact]
    public async Task ATextIsScheduledAsTheAuthorsDraftAndAuditedInTheSameStep()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1, assignedTo: test.Bed.MemberId);

        var result = await test.CreateHandler().HandleAsync(TextCommand(test, conversation, "  Hola {saludo}  "), Ct);

        Assert.True(result.Created);
        var row = Assert.Single(test.Scheduled.Rows);
        Assert.Equal(("Hola {saludo}", ScheduledMessageStatus.Scheduled, MessageKind.Text, test.Bed.MemberId), (row.Text, row.Status, row.Kind, row.AuthorMemberId));
        Assert.Equal((row.Id, "Scheduled", "Text", "Hola {saludo}", false, 1L), (result.Body.Id, result.Body.Status, result.Body.Kind, result.Body.Text, result.Body.CanRetry, result.Body.Version));
        Assert.Equal(("Andrés", "Andrés", (string?)null), (result.Body.Author.DisplayName, result.Body.CreatedBy.DisplayName, result.Body.UpdatedBy?.DisplayName));
        var audit = Assert.Single(test.Audit.ScheduledMessages);
        Assert.Equal(
            new ScheduledMessageAuditCall(test.Bed.TenantId, test.Bed.SubjectId, false, MessagingAuditActions.ScheduledMessageCreated, row.Id, null), audit);
        // Programar no manda nada ni reclama una fila de mensaje.
        Assert.Empty(test.Bed.Outbound.Claims);
        Assert.Empty(test.Bed.Meta.StreamUploads);
    }

    [Theory]
    [InlineData(ScheduledMessageStatus.Scheduled)]
    [InlineData(ScheduledMessageStatus.Failed)]
    [InlineData(ScheduledMessageStatus.Sent)]
    [InlineData(ScheduledMessageStatus.Cancelled)]
    public async Task AnExistingClientIdReturnsTheRowWithoutStagingAdmissionOrTimeChecks(ScheduledMessageStatus status)
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1, resolved: true);
        var existing = RowIn(test, conversation, status);

        // Hora pasada, conversación resuelta y un adjunto que no existe: nada de eso se mira.
        var result = await test.CreateHandler().HandleAsync(
            MediaCommand(test, conversation, Guid.CreateVersion7(), "a.pdf", at: test.Bed.Now.AddDays(-3)), Ct);

        Assert.False(result.Created);
        Assert.Equal((existing.Id, status.ToString()), (result.Body.Id, result.Body.Status));
        Assert.Empty(test.Bed.Staging.Stats);
        Assert.Empty(test.Bed.Repository.AutoAssigned);
        Assert.Equal(0, test.Scheduled.Adds);
        Assert.Empty(test.Audit.ScheduledMessages);
    }

    [Fact]
    public async Task ABlockedContactCannotSchedule()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1, assignedTo: test.Bed.MemberId, blocked: true);

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => test.CreateHandler().HandleAsync(TextCommand(test, conversation), Ct));

        Assert.Equal(MessagingErrorCodes.ContactBlocked, error.Code);
        Assert.Empty(test.Scheduled.Rows);
    }

    [Fact]
    public async Task AResolvedConversationCannotSchedule()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1, resolved: true, assignedTo: test.Bed.MemberId);

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => test.CreateHandler().HandleAsync(TextCommand(test, conversation), Ct));

        Assert.Equal(MessagingErrorCodes.ConversationNotOpen, error.Code);
        Assert.Empty(test.Scheduled.Rows);
    }

    [Fact]
    public async Task AnotherAssigneesConversationCannotSchedule()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1, assignedTo: Guid.CreateVersion7());

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => test.CreateHandler().HandleAsync(TextCommand(test, conversation), Ct));

        Assert.Equal(MessagingErrorCodes.AssignedToOther, error.Code);
        Assert.Empty(test.Scheduled.Rows);
    }

    [Fact]
    public async Task SchedulingAnUnassignedConversationTakesIt()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1);

        var result = await test.CreateHandler().HandleAsync(TextCommand(test, conversation), Ct);

        Assert.True(result.Created);
        Assert.Equal(conversation.Id, Assert.Single(test.Bed.Repository.AutoAssigned));
    }

    [Theory]
    [InlineData(30, "messaging.scheduled.too_soon")]
    [InlineData(23 * 3600 + 3 * 60, "messaging.scheduled.outside_window")]
    public async Task TooSoonAndOutsideWindowAreRejectedBeforeAdmission(int secondsAhead, string code)
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1);

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() =>
            test.CreateHandler().HandleAsync(TextCommand(test, conversation, at: test.Bed.Now.AddSeconds(secondsAhead)), Ct));

        Assert.Equal(code, error.Code);
        Assert.Empty(test.Bed.Repository.AutoAssigned);
        Assert.Empty(test.Scheduled.Rows);
    }

    [Fact]
    public async Task ASecondScheduledMessageIsAConflictBeforeAdmissionAndLeavesTheStaging()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1);
        RowIn(test, conversation, ScheduledMessageStatus.Scheduled, clientId: Guid.CreateVersion7());
        var uploadId = Guid.CreateVersion7();
        var stagingKey = test.Bed.Stage(conversation.Id, uploadId, "a.pdf", Pdf);

        var error = await Assert.ThrowsAsync<ResourceConflictException>(() =>
            test.CreateHandler().HandleAsync(MediaCommand(test, conversation, uploadId, "a.pdf"), Ct));

        Assert.Equal(MessagingErrorCodes.ScheduledAlreadyExists, error.Code);
        Assert.Empty(test.Bed.Repository.AutoAssigned);
        Assert.Contains(stagingKey, test.Bed.Store.Objects.Keys);
        Assert.Empty(test.Bed.Store.Deleted);
    }

    [Fact]
    public async Task AMissingStagingIsUploadNotFound()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1, assignedTo: test.Bed.MemberId);

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() =>
            test.CreateHandler().HandleAsync(MediaCommand(test, conversation, Guid.CreateVersion7(), "a.pdf"), Ct));

        Assert.Equal(MessagingErrorCodes.MediaUploadNotFound, error.Code);
        Assert.Empty(test.Scheduled.Rows);
    }

    [Fact]
    public async Task AnOversizedFileIsTooLargeAndItsStagingIsDeleted()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1, assignedTo: test.Bed.MemberId);
        var uploadId = Guid.CreateVersion7();
        var stagingKey = test.Bed.Stage(conversation.Id, uploadId, "a.pdf", Pdf);
        test.Bed.Staging.StatSize = 1024L * 1024 * 1024;

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() =>
            test.CreateHandler().HandleAsync(MediaCommand(test, conversation, uploadId, "a.pdf"), Ct));

        Assert.Equal(MessagingErrorCodes.MediaTooLarge, error.Code);
        Assert.Equal([stagingKey], test.Bed.Store.Deleted);
        Assert.Empty(test.Scheduled.Rows);
    }

    [Fact]
    public async Task AWrongSignatureIsUnsupportedFormatAndItsStagingIsDeleted()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1, assignedTo: test.Bed.MemberId);
        var uploadId = Guid.CreateVersion7();
        var stagingKey = test.Bed.Stage(conversation.Id, uploadId, "a.pdf", Exe);

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() =>
            test.CreateHandler().HandleAsync(MediaCommand(test, conversation, uploadId, "a.pdf"), Ct));

        Assert.Equal(MessagingErrorCodes.MediaUnsupportedFormat, error.Code);
        Assert.Equal([stagingKey], test.Bed.Store.Deleted);
        Assert.Empty(test.Scheduled.Rows);
    }

    [Fact]
    public async Task AnImageIsCompressedStoredUnderScheduledAndTheStagingDeleted()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1, assignedTo: test.Bed.MemberId);
        var uploadId = Guid.CreateVersion7();
        var stagingKey = test.Bed.Stage(conversation.Id, uploadId, "Foto.PNG", Png);

        var result = await test.CreateHandler().HandleAsync(MediaCommand(test, conversation, uploadId, "Foto.PNG", "  Mira {saludo}  "), Ct);

        Assert.True(result.Created);
        var row = Assert.Single(test.Scheduled.Rows);
        var key = ScheduledKey(test, row.Id, ".jpg");
        Assert.Equal(key, row.MediaStorageKey);
        var stored = Assert.Single(test.Bed.Store.Objects);
        Assert.Equal((key, "image/jpeg"), (stored.Key, stored.Value.ContentType));
        Assert.Equal(FakeImageCompressor.Output, stored.Value.Bytes);
        Assert.Equal([stagingKey], test.Bed.Store.Deleted);
        Assert.Equal(new ScheduledMediaDto("image/jpeg", "Foto.jpg", FakeImageCompressor.Output.Length), result.Body.Media);
        Assert.Equal(("Image", "Mira {saludo}", false, (string?)null), (result.Body.Kind, result.Body.Caption, result.Body.IsVoiceNote, result.Body.Text));
        // Lo que se guarda y se mide es la comprimida, no el original.
        Assert.Equal((long)FakeImageCompressor.Output.Length, row.MediaSizeBytes);
        Assert.Equal(MessagingAuditActions.ScheduledMessageCreated, Assert.Single(test.Audit.ScheduledMessages).Action);
    }

    [Fact]
    public async Task ADocumentIsPromotedUnderScheduledWithItsOwnExtensionAndNothingGoesToMeta()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1, assignedTo: test.Bed.MemberId);
        var uploadId = Guid.CreateVersion7();
        var stagingKey = test.Bed.Stage(conversation.Id, uploadId, "Catalogo.PDF", Pdf);

        var result = await test.CreateHandler().HandleAsync(MediaCommand(test, conversation, uploadId, "Catalogo.PDF"), Ct);

        var row = Assert.Single(test.Scheduled.Rows);
        var key = ScheduledKey(test, row.Id, ".pdf");
        Assert.Equal((stagingKey, key, FakeUploadStaging.ETag), Assert.Single(test.Bed.Staging.Promoted));
        Assert.Contains(key, test.Bed.Store.Objects.Keys);
        Assert.Equal([stagingKey], test.Bed.Store.Deleted);
        Assert.Equal(new ScheduledMediaDto("application/pdf", "Catalogo.PDF", Pdf.Length), result.Body.Media);
        Assert.Equal("Document", result.Body.Kind);
        Assert.Empty(test.Bed.Meta.StreamUploads);
    }

    [Fact]
    public async Task RetryAfterLosingThe201ReturnsTheSameRowNotUploadNotFound()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1, assignedTo: test.Bed.MemberId);
        var uploadId = Guid.CreateVersion7();
        test.Bed.Stage(conversation.Id, uploadId, "Foto.png", Png);
        var command = MediaCommand(test, conversation, uploadId, "Foto.png");

        var first = await test.CreateHandler().HandleAsync(command, Ct);
        var second = await test.CreateHandler().HandleAsync(command, Ct);

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(first.Body.Id, second.Body.Id);
        Assert.Single(test.Scheduled.Rows);
        Assert.Single(test.Bed.Staging.Stats);
        Assert.Single(test.Audit.ScheduledMessages);
    }

    [Fact]
    public async Task ARepeatedRequestThatRacedPastTheFirstLookupReturnsTheWinnerWhenTheStagingIsGone()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1, assignedTo: test.Bed.MemberId);
        var winner = RowIn(test, conversation, ScheduledMessageStatus.Scheduled);
        test.Scheduled.Rows.Clear();
        var lookups = new LateWinnerRepository(test.Scheduled, winner);

        var handler = new CreateScheduledMessageHandler(
            lookups, test.Bed.Conversations, test.Bed.Repository, test.Bed.Messages, test.Bed.Outbound, test.Bed.Meta, test.Bed.Store, test.Bed.Staging,
            test.Bed.Compressor, test.Bed.Connections, new FakeMemberNames(test.Bed.MemberId), MembershipOf(test), test.Bed.TenantModules,
            new FakeExecutionContext(test.Bed.TenantId, test.Bed.SubjectId, MessagingPermissions.ConversationManage), new FakeClock(test.Bed.Now),
            test.Bed.Notifier, test.Bed.Receipts, new FakeChatbotNames(), test.Audit, new ScheduledMessageMapper(new FakeMemberNames(test.Bed.MemberId)),
            new CreateScheduledMessageValidator());

        var result = await handler.HandleAsync(MediaCommand(test, conversation, Guid.CreateVersion7(), "a.pdf"), Ct);

        Assert.False(result.Created);
        Assert.Equal(winner.Id, result.Body.Id);
    }

    [Fact]
    public async Task TheRaceOnTheClientIdIndexReturnsTheWinner()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1, assignedTo: test.Bed.MemberId);
        var uploadId = Guid.CreateVersion7();
        test.Bed.Stage(conversation.Id, uploadId, "Foto.png", Png);
        var winner = ScheduledMessage.CreateText(
            Guid.CreateVersion7(), test.Bed.TenantId, conversation.Id, test.Bed.ClientId, "Gané", test.Bed.Now.AddHours(3), test.Bed.MemberId, test.Bed.Now);
        test.Scheduled.RaceWinner = winner;

        var result = await test.CreateHandler().HandleAsync(MediaCommand(test, conversation, uploadId, "Foto.png"), Ct);

        Assert.False(result.Created);
        Assert.Equal(winner.Id, result.Body.Id);
        // La copia de este request se borró; el staging se queda: lo consume quien ganó, no éste.
        Assert.DoesNotContain(test.Bed.Store.Objects.Keys, key => key.StartsWith("messaging/scheduled/", StringComparison.Ordinal));
        Assert.Contains(test.Bed.Store.Deleted, key => key.StartsWith("messaging/scheduled/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AConflictOnAddKeepsTheStagingDeletesTheScheduledCopyAndRethrows()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1, assignedTo: test.Bed.MemberId);
        var uploadId = Guid.CreateVersion7();
        var stagingKey = test.Bed.Stage(conversation.Id, uploadId, "Foto.png", Png);
        test.Scheduled.FailAddAsAlreadyExists = true;

        var error = await Assert.ThrowsAsync<ResourceConflictException>(() =>
            test.CreateHandler().HandleAsync(MediaCommand(test, conversation, uploadId, "Foto.png"), Ct));

        Assert.Equal(MessagingErrorCodes.ScheduledAlreadyExists, error.Code);
        Assert.Contains(stagingKey, test.Bed.Store.Objects.Keys);
        Assert.DoesNotContain(stagingKey, test.Bed.Store.Deleted);
        Assert.DoesNotContain(test.Bed.Store.Objects.Keys, key => key.StartsWith("messaging/scheduled/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AStorageFailureWhileCopyingIsUploadFailedAndKeepsTheStaging()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1, assignedTo: test.Bed.MemberId);
        var uploadId = Guid.CreateVersion7();
        var stagingKey = test.Bed.Stage(conversation.Id, uploadId, "a.pdf", Pdf);
        test.Bed.Staging.PromoteFailure = new InvalidOperationException("r2 down");

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() =>
            test.CreateHandler().HandleAsync(MediaCommand(test, conversation, uploadId, "a.pdf"), Ct));

        Assert.Equal(MessagingErrorCodes.MediaUploadFailed, error.Code);
        Assert.Contains(stagingKey, test.Bed.Store.Objects.Keys);
        Assert.Empty(test.Scheduled.Rows);
    }

    [Fact]
    public async Task InstagramRejectsTextOver1000MeasuredWithTheGreeting()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1, userId: "17841400000000099", waId: null, channel: ConversationChannel.Instagram);

        // 989 + {saludo}: 997 en bruto (cabe), 1002 ya resuelto (no cabe).
        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            test.CreateHandler().HandleAsync(TextCommand(test, conversation, new string('a', 989) + "{saludo}"), Ct));

        Assert.Contains(error.Errors, failure => failure.PropertyName == "text");
        Assert.Empty(test.Bed.Repository.AutoAssigned);
        Assert.Empty(test.Scheduled.Rows);
    }

    [Fact]
    public async Task InstagramAcceptsTextThatMeasures1000WithTheGreeting()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(
            lastInboundHoursAgo: 1, assignedTo: test.Bed.MemberId, userId: "17841400000000099", waId: null, channel: ConversationChannel.Instagram);

        var result = await test.CreateHandler().HandleAsync(TextCommand(test, conversation, new string('a', 987) + "{saludo}"), Ct);

        Assert.True(result.Created);
    }

    [Fact]
    public async Task InstagramRejectsAFormatItDoesNotAccept()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(
            lastInboundHoursAgo: 1, assignedTo: test.Bed.MemberId, userId: "17841400000000099", waId: null, channel: ConversationChannel.Instagram);
        var uploadId = Guid.CreateVersion7();
        test.Bed.Stage(conversation.Id, uploadId, "a.docx", [0x50, 0x4B, 0x03, 0x04, 1, 2, 3]);

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() =>
            test.CreateHandler().HandleAsync(MediaCommand(test, conversation, uploadId, "a.docx"), Ct));

        Assert.Equal(MessagingErrorCodes.MediaUnsupportedFormat, error.Code);
        Assert.Empty(test.Bed.Staging.Stats);
    }

    [Fact]
    public async Task AnotherTenantOrAReaderCannotSchedule()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1, assignedTo: test.Bed.MemberId);

        await Assert.ThrowsAsync<RequestForbiddenException>(() => test.CreateHandler(tenantId: Guid.CreateVersion7()).HandleAsync(TextCommand(test, conversation), Ct));
        await Assert.ThrowsAsync<RequestForbiddenException>(() => test.CreateHandler(canManage: false).HandleAsync(TextCommand(test, conversation), Ct));

        Assert.Empty(test.Scheduled.Rows);
    }

    [Theory]
    [InlineData(60, false, true)]
    [InlineData(23 * 60, false, true)]
    [InlineData(24 * 60 - 3, false, true)] // 23h57m: cabe con el margen de 2 min
    [InlineData(24 * 60 - 1, false, false)] // 23h59m: la ventana de 24 h sigue abierta, pero el margen (D7) ya la cerró
    [InlineData(24 * 60, false, false)]
    [InlineData(60, true, false)]
    public async Task GetReturnsOnlyActiveRowsAndCanRetryFollowsTheWindow(int lastInboundMinutesAgo, bool blocked, bool canRetry)
    {
        var test = new ScheduledMessageTestBed();
        var opened = test.Bed.OpenConversation(lastInboundHoursAgo: 1, blocked: blocked);
        var conversation = opened with { LastInboundAt = test.Bed.Now.AddMinutes(-lastInboundMinutesAgo) };
        test.Bed.Conversations.Rows[test.Bed.Conversations.Rows.IndexOf(opened)] = conversation;
        // Un terminal no cuenta: no hay activo todavía.
        RowIn(test, conversation, ScheduledMessageStatus.Sent, clientId: Guid.CreateVersion7());
        Assert.Null((await test.GetHandler().HandleAsync(new GetScheduledMessageQuery(test.Bed.TenantId, conversation.Id), Ct)).ScheduledMessage);

        var failed = RowIn(test, conversation, ScheduledMessageStatus.Failed, clientId: Guid.CreateVersion7());

        var body = (await test.GetHandler().HandleAsync(new GetScheduledMessageQuery(test.Bed.TenantId, conversation.Id), Ct)).ScheduledMessage;

        Assert.NotNull(body);
        Assert.Equal((failed.Id, "Failed", MessagingErrorCodes.ScheduledUnknownOutcome, canRetry), (body.Id, body.Status, body.FailureCode, body.CanRetry));
    }

    [Fact]
    public async Task GetRequiresReadAndAnExistingConversation()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1);

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            test.GetHandler(Guid.CreateVersion7()).HandleAsync(new GetScheduledMessageQuery(test.Bed.TenantId, conversation.Id), Ct));
        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            test.GetHandler(null, MessagingPermissions.ConversationManage).HandleAsync(new GetScheduledMessageQuery(test.Bed.TenantId, conversation.Id), Ct));
        await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            test.GetHandler().HandleAsync(new GetScheduledMessageQuery(test.Bed.TenantId, Guid.CreateVersion7()), Ct));
    }

    private static FakeMembershipDirectory MembershipOf(ScheduledMessageTestBed test)
    {
        var membership = new FakeMembershipDirectory();
        membership.Active[(test.Bed.SubjectId, test.Bed.TenantId)] = test.Bed.MemberId;
        return membership;
    }

    /// <summary>La primera consulta por <c>clientId</c> no ve nada (el otro request todavía no había guardado) y la segunda sí.</summary>
    private sealed class LateWinnerRepository(InMemoryScheduledMessageRepository inner, ScheduledMessage winner) : IScheduledMessageRepository
    {
        private int _lookups;

        public Task<ScheduledMessage?> FindByClientIdAsync(Guid tenantId, Guid conversationId, Guid clientId, CancellationToken cancellationToken) =>
            Task.FromResult<ScheduledMessage?>(++_lookups > 1 ? winner : null);

        public Task<ScheduledMessage?> FindActiveAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) =>
            inner.FindActiveAsync(tenantId, conversationId, cancellationToken);

        public Task<IScheduledMessageLock?> LockAsync(Guid tenantId, Guid conversationId, Guid scheduledMessageId, CancellationToken cancellationToken) =>
            inner.LockAsync(tenantId, conversationId, scheduledMessageId, cancellationToken);

        public Task AddAsync(ScheduledMessage message, CancellationToken cancellationToken) => inner.AddAsync(message, cancellationToken);
    }
}
