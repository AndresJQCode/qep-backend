using System.Security.Cryptography;
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-11-09: <see cref="CreateScheduledVoiceNoteHandler"/> sigue el orden de <see cref="CreateScheduledMessageHandler"/>
/// (el <c>clientId</c> ya guardado primero, sin transcodificar ni admitir), guarda la nota con la misma forma que
/// <see cref="SendAudioMessageHandler"/> la manda y nunca lleva pie de foto.</summary>
public sealed class CreateScheduledVoiceNoteHandlerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly byte[] Voice = OggOpusFixture.VoiceNote();

    private static CreateScheduledVoiceNoteCommand Command(
        ScheduledMessageTestBed test, ConversationRow conversation, byte[]? audio = null, DateTimeOffset? at = null) =>
        new(test.Bed.TenantId, conversation.Id, test.Bed.ClientId, at ?? test.Bed.Now.AddHours(2), audio ?? Voice);

    private static string ScheduledKey(ScheduledMessageTestBed test, Guid id, string extension) =>
        $"messaging/scheduled/{test.Bed.TenantId}/{id}{extension}";

    [Fact]
    public async Task AVoiceNoteIsStoredAsOggUnderScheduled()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1, assignedTo: test.Bed.MemberId);

        var result = await test.CreateVoiceNoteHandler().HandleAsync(Command(test, conversation), Ct);

        Assert.True(result.Created);
        var row = Assert.Single(test.Scheduled.Rows);
        var key = ScheduledKey(test, row.Id, ".ogg");
        var stored = Assert.Single(test.Bed.Store.Objects);
        Assert.Equal((key, OggOpusSniffer.MimeType), (stored.Key, stored.Value.ContentType));
        Assert.Equal(Voice, stored.Value.Bytes);
        Assert.Equal(
            (MessageKind.Audio, true, (string?)null, (string?)null, key, OggOpusSniffer.MimeType, (long)Voice.Length, Convert.ToHexStringLower(SHA256.HashData(Voice))),
            (row.Kind, row.IsVoiceNote, row.Caption, row.MediaFileName, row.MediaStorageKey, row.MediaMimeType, row.MediaSizeBytes, row.MediaSha256));
        Assert.Equal(
            ("Audio", true, (string?)null, (string?)null, ScheduledMessageStatus.Scheduled.ToString()),
            (result.Body.Kind, result.Body.IsVoiceNote, result.Body.Caption, result.Body.Text, result.Body.Status));
        Assert.Equal(new ScheduledMediaDto(OggOpusSniffer.MimeType, null, Voice.Length), result.Body.Media);
        // Programar no manda nada: ni a Meta, ni al transcodificador, ni reclama una fila de mensaje.
        Assert.Empty(test.Bed.Meta.StreamUploads);
        Assert.Empty(test.Bed.Transcoder.Inputs);
        Assert.Empty(test.Bed.Outbound.Claims);
        Assert.Empty(test.Bed.Staging.Stats);
    }

    [Fact]
    public async Task AVoiceNoteIsAuditedAsCreatedInTheSameStep()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1, assignedTo: test.Bed.MemberId);

        await test.CreateVoiceNoteHandler().HandleAsync(Command(test, conversation), Ct);

        var row = Assert.Single(test.Scheduled.Rows);
        Assert.Equal(
            new ScheduledMessageAuditCall(test.Bed.TenantId, test.Bed.SubjectId, false, MessagingAuditActions.ScheduledMessageCreated, row.Id, null),
            Assert.Single(test.Audit.ScheduledMessages));
    }

    [Fact]
    public async Task InstagramStoresTheVoiceNoteTranscodedToWav()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(
            lastInboundHoursAgo: 1, assignedTo: test.Bed.MemberId, userId: "17841400000000099", waId: null, channel: ConversationChannel.Instagram);

        var result = await test.CreateVoiceNoteHandler().HandleAsync(Command(test, conversation), Ct);

        Assert.True(result.Created);
        var row = Assert.Single(test.Scheduled.Rows);
        byte[] wav = [.. FakeOpusTranscoder.WavMarker, .. Voice];
        var key = ScheduledKey(test, row.Id, ".wav");
        var stored = Assert.Single(test.Bed.Store.Objects);
        Assert.Equal((key, InstagramAudio.WavMimeType), (stored.Key, stored.Value.ContentType));
        Assert.Equal(wav, stored.Value.Bytes);
        Assert.Equal(InstagramAudio.WavMimeType, row.MediaMimeType);
        Assert.Equal(((long)wav.Length, Convert.ToHexStringLower(SHA256.HashData(wav))), (row.MediaSizeBytes, row.MediaSha256));
        Assert.Equal((MessageKind.Audio, true, (string?)null), (row.Kind, row.IsVoiceNote, row.MediaFileName));
        Assert.Equal(new ScheduledMediaDto(InstagramAudio.WavMimeType, null, wav.Length), result.Body.Media);
        Assert.Equal(Voice, Assert.Single(test.Bed.Transcoder.Inputs));
    }

    [Fact]
    public async Task AnInstagramVoiceNoteThatDoesNotDecodeIsAFileErrorBeforeAdmission()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1, userId: "17841400000000099", waId: null, channel: ConversationChannel.Instagram);
        test.Bed.Transcoder.Failure = new InvalidDataException("corrupt");

        var error = await Assert.ThrowsAsync<ValidationException>(() => test.CreateVoiceNoteHandler().HandleAsync(Command(test, conversation), Ct));

        Assert.Contains(error.Errors, failure => failure.PropertyName == "file");
        Assert.Empty(test.Bed.Repository.AutoAssigned);
        Assert.Empty(test.Scheduled.Rows);
        Assert.Empty(test.Bed.Store.Objects);
    }

    [Fact]
    public async Task RetryingTheSameClientIdReturnsTheStoredRow()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1, assignedTo: test.Bed.MemberId);
        var command = Command(test, conversation);

        var first = await test.CreateVoiceNoteHandler().HandleAsync(command, Ct);
        var second = await test.CreateVoiceNoteHandler().HandleAsync(command, Ct);

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(first.Body.Id, second.Body.Id);
        Assert.Single(test.Scheduled.Rows);
        Assert.Equal(1, test.Scheduled.Adds);
        // 3b: sin segunda subida, y un solo `.created`.
        Assert.Single(test.Bed.Store.Objects);
        Assert.Single(test.Audit.ScheduledMessages);
    }

    [Fact]
    public async Task AnExistingClientIdIsReturnedWithoutTranscodingAdmissionOrTimeChecks()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(
            lastInboundHoursAgo: 1, resolved: true, userId: "17841400000000099", waId: null, channel: ConversationChannel.Instagram);
        var existing = ScheduledMessage.CreateText(
            Guid.CreateVersion7(), test.Bed.TenantId, conversation.Id, test.Bed.ClientId, "Hola", test.Bed.Now.AddHours(2), test.Bed.MemberId, test.Bed.Now);
        test.Scheduled.Rows.Add(existing);

        var result = await test.CreateVoiceNoteHandler().HandleAsync(Command(test, conversation, at: test.Bed.Now.AddDays(-3)), Ct);

        Assert.False(result.Created);
        Assert.Equal(existing.Id, result.Body.Id);
        Assert.Empty(test.Bed.Transcoder.Inputs);
        Assert.Empty(test.Bed.Repository.AutoAssigned);
        Assert.Empty(test.Bed.Store.Objects);
        Assert.Equal(0, test.Scheduled.Adds);
    }

    [Fact]
    public async Task ANonOpusFileIsAFileValidationError()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1, assignedTo: test.Bed.MemberId);

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            test.CreateVoiceNoteHandler().HandleAsync(Command(test, conversation, OggOpusFixture.WebmHeader()), Ct));

        Assert.Contains(error.Errors, failure => failure.PropertyName == "file");
        Assert.Empty(test.Scheduled.Rows);
        Assert.Empty(test.Bed.Store.Objects);
    }

    [Fact]
    public async Task ScheduledAtAndFileAreRequired()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1, assignedTo: test.Bed.MemberId);

        var missingTime = await Assert.ThrowsAsync<ValidationException>(() =>
            test.CreateVoiceNoteHandler().HandleAsync(Command(test, conversation) with { ScheduledAt = null }, Ct));
        var missingFile = await Assert.ThrowsAsync<ValidationException>(() =>
            test.CreateVoiceNoteHandler().HandleAsync(Command(test, conversation) with { Audio = null }, Ct));
        var missingClient = await Assert.ThrowsAsync<ValidationException>(() =>
            test.CreateVoiceNoteHandler().HandleAsync(Command(test, conversation) with { ClientId = null }, Ct));

        Assert.Contains(missingTime.Errors, failure => failure.PropertyName == "scheduledAt");
        Assert.Contains(missingFile.Errors, failure => failure.PropertyName == "file");
        Assert.Contains(missingClient.Errors, failure => failure.PropertyName == "clientId");
    }

    [Theory]
    [InlineData(30, "messaging.scheduled.too_soon")]
    [InlineData(23 * 3600 + 3 * 60, "messaging.scheduled.outside_window")]
    public async Task TooSoonAndOutsideWindowAreRejectedBeforeAdmissionAndStorage(int secondsAhead, string code)
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1);

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() =>
            test.CreateVoiceNoteHandler().HandleAsync(Command(test, conversation, at: test.Bed.Now.AddSeconds(secondsAhead)), Ct));

        Assert.Equal(code, error.Code);
        Assert.Empty(test.Bed.Repository.AutoAssigned);
        Assert.Empty(test.Bed.Store.Objects);
    }

    [Fact]
    public async Task ASecondScheduledMessageIsAConflictBeforeAdmissionOrStorage()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1);
        test.Scheduled.Rows.Add(ScheduledMessage.CreateText(
            Guid.CreateVersion7(), test.Bed.TenantId, conversation.Id, Guid.CreateVersion7(), "Hola", test.Bed.Now.AddHours(2), test.Bed.MemberId, test.Bed.Now));

        var error = await Assert.ThrowsAsync<ResourceConflictException>(() => test.CreateVoiceNoteHandler().HandleAsync(Command(test, conversation), Ct));

        Assert.Equal(MessagingErrorCodes.ScheduledAlreadyExists, error.Code);
        Assert.Empty(test.Bed.Repository.AutoAssigned);
        Assert.Empty(test.Bed.Store.Objects);
    }

    [Fact]
    public async Task ABlockedContactAnotherAssigneeAndAReaderCannotSchedule()
    {
        var test = new ScheduledMessageTestBed();
        var blocked = test.Bed.OpenConversation(lastInboundHoursAgo: 1, assignedTo: test.Bed.MemberId, blocked: true);
        var other = test.Bed.OpenConversation(lastInboundHoursAgo: 1, assignedTo: Guid.CreateVersion7());

        var blockedError = await Assert.ThrowsAsync<MessagingDomainException>(() => test.CreateVoiceNoteHandler().HandleAsync(Command(test, blocked), Ct));
        var otherError = await Assert.ThrowsAsync<MessagingDomainException>(() => test.CreateVoiceNoteHandler().HandleAsync(Command(test, other), Ct));
        await Assert.ThrowsAsync<RequestForbiddenException>(() => test.CreateVoiceNoteHandler(canManage: false).HandleAsync(Command(test, blocked), Ct));

        Assert.Equal(MessagingErrorCodes.ContactBlocked, blockedError.Code);
        Assert.Equal(MessagingErrorCodes.AssignedToOther, otherError.Code);
        Assert.Empty(test.Scheduled.Rows);
        Assert.Empty(test.Bed.Store.Objects);
    }

    [Fact]
    public async Task AStorageFailureIsUploadFailedAndLeavesNoRow()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1, assignedTo: test.Bed.MemberId);
        test.Bed.Store.UploadFailure = new InvalidOperationException("r2 down");

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => test.CreateVoiceNoteHandler().HandleAsync(Command(test, conversation), Ct));

        Assert.Equal(MessagingErrorCodes.MediaUploadFailed, error.Code);
        Assert.Empty(test.Scheduled.Rows);
        Assert.Empty(test.Audit.ScheduledMessages);
    }

    [Fact]
    public async Task AConflictOnAddDeletesTheStoredCopyAndRethrows()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1, assignedTo: test.Bed.MemberId);
        test.Scheduled.FailAddAsAlreadyExists = true;

        var error = await Assert.ThrowsAsync<ResourceConflictException>(() => test.CreateVoiceNoteHandler().HandleAsync(Command(test, conversation), Ct));

        Assert.Equal(MessagingErrorCodes.ScheduledAlreadyExists, error.Code);
        Assert.Empty(test.Bed.Store.Objects);
        Assert.Contains(test.Bed.Store.Deleted, key => key.StartsWith("messaging/scheduled/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheRaceOnTheClientIdIndexReturnsTheWinnerAndDeletesTheCopy()
    {
        var test = new ScheduledMessageTestBed();
        var conversation = test.Bed.OpenConversation(lastInboundHoursAgo: 1, assignedTo: test.Bed.MemberId);
        var winner = ScheduledMessage.CreateText(
            Guid.CreateVersion7(), test.Bed.TenantId, conversation.Id, test.Bed.ClientId, "Gané", test.Bed.Now.AddHours(3), test.Bed.MemberId, test.Bed.Now);
        test.Scheduled.RaceWinner = winner;

        var result = await test.CreateVoiceNoteHandler().HandleAsync(Command(test, conversation), Ct);

        Assert.False(result.Created);
        Assert.Equal(winner.Id, result.Body.Id);
        Assert.Empty(test.Bed.Store.Objects);
    }
}
