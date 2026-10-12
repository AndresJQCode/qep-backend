using System.Security.Cryptography;
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-11 §6.3 y §10: mismas guardas y mismo orden que el texto; dentro del reclamo R2 → subida a
/// Meta → envío → la tabla del texto. D-V1: el Failed guarda el medio. Review Focus 1: un rollback sólo borra de R2 lo
/// que subió una fila nueva.</summary>
public sealed class SendAudioMessageHandlerTests
{
    private static readonly byte[] Voice = OggOpusFixture.VoiceNote();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SendAudioMessageCommand Command(MessagingTestBed bed, Guid conversationId, Guid? replyTo = null, byte[]? audio = null) =>
        new(bed.TenantId, conversationId, bed.ClientId, audio ?? Voice, replyTo);

    [Fact]
    public async Task AHappyVoiceNoteIsStoredUploadedSentAsVoiceAndCommittedWithTheMediaId()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1, assignedTo: bed.MemberId);
        bed.Meta.NextUpload = new MessagingGraphResult<string>("media.out", null);
        bed.Meta.NextSend = new SendResult(SendOutcome.Sent, "wamid.voice", null, null);

        var message = await bed.SendAudioHandler().HandleAsync(Command(bed, conversation.Id), Ct);

        Assert.Equal(("Audio", "Sent", (string?)null, bed.ClientId), (message.Kind, message.Status, message.Text, message.ClientId));
        Assert.Equal(new MediaDto($"/api/v1/tenants/{bed.TenantId}/messaging/media/{message.Id}", "audio/ogg", null, null), message.Media);
        var stored = Assert.Single(bed.Store.Objects);
        Assert.Equal(($"messaging/{bed.TenantId}/{message.Id}", "audio/ogg"), (stored.Key, stored.Value.ContentType));
        Assert.Equal(Voice, stored.Value.Bytes);
        var upload = Assert.Single(bed.Meta.Uploads);
        Assert.Equal("audio/ogg", upload.MimeType);
        Assert.Equal(Voice, upload.Content);
        var send = Assert.Single(bed.Meta.AudioSends);
        Assert.Equal(("media.out", $"qep:{message.Id}", (string?)null), (send.MediaId, send.CallbackData, send.ContextWamid));
        Assert.Equal((SendTarget)new SendToPhone(conversation.WaId!), send.Target);
        Assert.Empty(bed.Meta.Sends);
        var claim = Assert.Single(bed.Outbound.Claims);
        Assert.Equal(("committed-sent:wamid.voice", "media.out"), (claim.Outcome, claim.MetaMediaId));
        Assert.Equal((MessageKind.Audio, (string?)null), (claim.Draft.Kind, claim.Draft.Text));
        Assert.Equal(new OutboundMedia("audio/ogg", Voice.Length, Convert.ToHexStringLower(SHA256.HashData(Voice))), claim.Draft.Media);
    }

    [Theory]
    [InlineData("resolved", MessagingErrorCodes.ConversationNotOpen)]
    [InlineData("window-closed", MessagingErrorCodes.WindowClosed)]
    [InlineData("never-wrote", MessagingErrorCodes.WindowClosed)]
    [InlineData("assigned-to-other", MessagingErrorCodes.AssignedToOther)]
    [InlineData("connection-paused", MessagingErrorCodes.ConnectionUnavailable)]
    public async Task TheChecksHaveTheSameCodesAndOrderAsText(string scenario, string expected)
    {
        var bed = new MessagingTestBed();
        var conversation = scenario switch
        {
            "resolved" => bed.OpenConversation(lastInboundHoursAgo: 1, resolved: true),
            "window-closed" => bed.OpenConversation(lastInboundHoursAgo: 25),
            "never-wrote" => bed.OpenConversation(lastInboundHoursAgo: null),
            "assigned-to-other" => bed.OpenConversation(lastInboundHoursAgo: 1, assignedTo: Guid.CreateVersion7()),
            _ => bed.OpenConversation(lastInboundHoursAgo: 1),
        };
        if (scenario == "connection-paused")
        {
            bed.Connections.Sender = null;
        }

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => bed.SendAudioHandler().HandleAsync(Command(bed, conversation.Id), Ct));

        Assert.Equal(expected, error.Code);
        // Nada sube a R2 ni a Meta antes de pasar todas las guardas; la conexión se mira ya con el reclamo tomado.
        Assert.Empty(bed.Store.Objects);
        Assert.Empty(bed.Meta.Uploads);
        Assert.Empty(bed.Meta.AudioSends);
        Assert.Equal(scenario == "connection-paused" ? ["rolled-back"] : [], bed.Outbound.Claims.Select(claim => claim.Outcome));
    }

    [Fact]
    public async Task AnotherTenantIsForbiddenAMissingConversationIsNotFoundAndAWebmIsAValidationError()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            bed.SendAudioHandler(tenantId: Guid.CreateVersion7()).HandleAsync(Command(bed, conversation.Id), Ct));
        var missing = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            bed.SendAudioHandler().HandleAsync(Command(bed, Guid.CreateVersion7()), Ct));
        var webm = await Assert.ThrowsAsync<ValidationException>(() =>
            bed.SendAudioHandler().HandleAsync(Command(bed, conversation.Id, audio: OggOpusFixture.WebmHeader()), Ct));

        Assert.Equal(MessagingErrorCodes.ConversationNotFound, missing.Code);
        Assert.Contains(webm.Errors, failure => failure.PropertyName == "file");
        Assert.Empty(bed.Outbound.Claims);
        Assert.Empty(bed.Store.Objects);
    }

    [Fact]
    public async Task AnR2FailureIs422UploadFailedWithoutRowNorMeta()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);
        bed.Store.UploadFailure = new IOException("R2 no responde");

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => bed.SendAudioHandler().HandleAsync(Command(bed, conversation.Id), Ct));

        Assert.Equal(MessagingErrorCodes.MediaUploadFailed, error.Code);
        Assert.IsType<IOException>(error.InnerException);
        Assert.Equal("rolled-back", Assert.Single(bed.Outbound.Claims).Outcome);
        Assert.Empty(bed.Meta.Uploads);
        Assert.Empty(bed.Meta.AudioSends);
    }

    [Theory]
    [InlineData(400, 100, "committed-failed:100", MessagingErrorCodes.MessageRejected, null)]
    [InlineData(500, null, "committed-failed:-1", MessagingErrorCodes.MessageRejected, null)]
    [InlineData(0, null, "committed-failed:-1", MessagingErrorCodes.MessageRejected, null)]
    [InlineData(200, null, "committed-failed:-1", MessagingErrorCodes.MessageRejected, null)]
    [InlineData(401, 190, "rolled-back", MessagingErrorCodes.ConnectionUnavailable, "token_expired")]
    [InlineData(400, 133010, "rolled-back", MessagingErrorCodes.ConnectionUnavailable, "number_unregistered")]
    public async Task AFailedUploadToMetaFollowsTheSendTableWithoutSending(int httpStatus, int? graphCode, string expectedClaim, string expectedCode, string? reported)
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);
        bed.Meta.NextUpload = new MessagingGraphResult<string>(null, new MessagingGraphFailure(httpStatus, graphCode, "x"));

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => bed.SendAudioHandler().HandleAsync(Command(bed, conversation.Id), Ct));

        Assert.Equal(expectedCode, error.Code);
        var claim = Assert.Single(bed.Outbound.Claims);
        Assert.Equal((expectedClaim, (string?)null), (claim.Outcome, claim.MetaMediaId));
        Assert.Empty(bed.Meta.AudioSends);
        Assert.Equal(reported is null ? [] : [reported], bed.Connections.Reported.Select(report => report.FailureCode));
        // Rollback de una fila nueva: el audio no tiene quién lo sirva y se borra. Failed: se queda (D-V1).
        var key = $"messaging/{bed.TenantId}/{claim.MessageId}";
        Assert.Equal(reported is null ? [] : [key], bed.Store.Deleted);
    }

    [Theory]
    [InlineData(131026, "committed-failed:131026", MessagingErrorCodes.MessageRejected)]
    [InlineData(131047, "committed-failed:131047", MessagingErrorCodes.WindowClosed)]
    public async Task ASendRejectedAfterTheUploadKeepsTheMediaIdAndTheAudio(int graphCode, string expectedClaim, string expectedCode)
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);
        bed.Meta.NextUpload = new MessagingGraphResult<string>("media.out", null);
        bed.Meta.NextSend = new SendResult(SendOutcome.GraphError, null, graphCode, "title");

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => bed.SendAudioHandler().HandleAsync(Command(bed, conversation.Id), Ct));

        Assert.Equal(expectedCode, error.Code);
        var claim = Assert.Single(bed.Outbound.Claims);
        Assert.Equal((expectedClaim, "media.out"), (claim.Outcome, claim.MetaMediaId));
        Assert.Single(bed.Store.Objects);
        Assert.Empty(bed.Store.Deleted);
    }

    [Fact]
    public async Task ARejectedCredentialOnTheSendRollsBackAndDeletesTheStoredAudio()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);
        bed.Meta.NextSend = new SendResult(SendOutcome.GraphError, null, 190, "title");

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => bed.SendAudioHandler().HandleAsync(Command(bed, conversation.Id), Ct));

        Assert.Equal(MessagingErrorCodes.ConnectionUnavailable, error.Code);
        var claim = Assert.Single(bed.Outbound.Claims);
        Assert.Equal("rolled-back", claim.Outcome);
        Assert.Equal([$"messaging/{bed.TenantId}/{claim.MessageId}"], bed.Store.Deleted);
        Assert.Empty(bed.Store.Objects);
    }

    [Fact]
    public async Task ARepeatedClientIdReturnsTheStoredVoiceNoteWithoutUploading()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);
        var stored = Guid.CreateVersion7();
        bed.Outbound.Committed = new ExistingOutbound(stored, MessageStatus.Delivered, bed.Now.AddMinutes(-5), bed.MemberId, "", null, MessageKind.Audio, "audio/ogg");

        var message = await bed.SendAudioHandler().HandleAsync(Command(bed, conversation.Id), Ct);

        Assert.Equal((stored, "Audio", "Delivered", (string?)null), (message.Id, message.Kind, message.Status, message.Text));
        Assert.Equal(new MediaDto($"/api/v1/tenants/{bed.TenantId}/messaging/media/{stored}", "audio/ogg", null, null), message.Media);
        Assert.Empty(bed.Outbound.Claims);
        Assert.Empty(bed.Store.Objects);
        Assert.Empty(bed.Meta.Uploads);
    }

    [Fact]
    public async Task AnInFlightClientIdIsResolvedByTheClaimWithoutUploading()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);
        bed.Outbound.Existing = new ExistingOutbound(Guid.CreateVersion7(), MessageStatus.Sent, bed.Now.AddMinutes(-1), bed.MemberId, "", null, MessageKind.Audio, "audio/ogg");

        var message = await bed.SendAudioHandler().HandleAsync(Command(bed, conversation.Id), Ct);

        Assert.Equal("Audio", message.Kind);
        Assert.Equal("rolled-back", Assert.Single(bed.Outbound.Claims).Outcome);
        Assert.Empty(bed.Store.Objects);
        Assert.Empty(bed.Meta.Uploads);
    }

    [Fact]
    public async Task ARetryOfAFailedVoiceNoteReusesTheMessageIdAndTheR2Key()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);
        var failed = Guid.CreateVersion7();
        bed.Outbound.Existing = new ExistingOutbound(failed, MessageStatus.Failed, bed.Now.AddMinutes(-5), bed.MemberId, "", null, MessageKind.Audio, "audio/ogg");

        var message = await bed.SendAudioHandler().HandleAsync(Command(bed, conversation.Id), Ct);

        Assert.Equal((failed, "Sent"), (message.Id, message.Status));
        Assert.Equal($"messaging/{bed.TenantId}/{failed}", Assert.Single(bed.Store.Objects).Key);
        Assert.Equal($"qep:{failed}", Assert.Single(bed.Meta.AudioSends).CallbackData);
    }

    // Review Focus 1: el objeto de R2 es el que la fila Failed ya sirve; borrarlo dejaría la burbuja muda.
    [Fact]
    public async Task ARejectedCredentialOnARetryKeepsTheAudioTheFailedRowServes()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);
        var failed = Guid.CreateVersion7();
        bed.Outbound.Existing = new ExistingOutbound(failed, MessageStatus.Failed, bed.Now.AddMinutes(-5), bed.MemberId, "", null, MessageKind.Audio, "audio/ogg");
        bed.Meta.NextUpload = new MessagingGraphResult<string>(null, new MessagingGraphFailure(401, 190, "x"));

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => bed.SendAudioHandler().HandleAsync(Command(bed, conversation.Id), Ct));

        Assert.Equal(MessagingErrorCodes.ConnectionUnavailable, error.Code);
        Assert.Equal("rolled-back", Assert.Single(bed.Outbound.Claims).Outcome);
        Assert.Empty(bed.Store.Deleted);
        Assert.Equal($"messaging/{bed.TenantId}/{failed}", Assert.Single(bed.Store.Objects).Key);
    }

    // Lo que escape después de subir a R2 deshace el reclamo de la fila nueva: el objeto no tendría fila que lo sirva.
    [Theory]
    [InlineData("cancelled")]
    [InlineData("unexpected")]
    public async Task AnExceptionAfterTheR2UploadOfANewRowDeletesTheStoredAudio(string scenario)
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);
        Exception thrown = scenario == "cancelled" ? new OperationCanceledException() : new InvalidOperationException("boom");
        bed.Meta.UploadException = thrown;

        var error = await Assert.ThrowsAnyAsync<Exception>(() => bed.SendAudioHandler().HandleAsync(Command(bed, conversation.Id), Ct));

        Assert.Same(thrown, error);
        var claim = Assert.Single(bed.Outbound.Claims);
        Assert.Equal([$"messaging/{bed.TenantId}/{claim.MessageId}"], bed.Store.Deleted);
        Assert.Equal([CancellationToken.None], bed.Store.DeleteTokens);
        Assert.Empty(bed.Store.Objects);
    }

    [Fact]
    public async Task AFailedDeleteDoesNotHideTheOriginalException()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);
        var thrown = new InvalidOperationException("boom");
        bed.Meta.UploadException = thrown;
        bed.Store.DeleteFailure = new IOException("R2 no responde");

        var error = await Assert.ThrowsAnyAsync<Exception>(() => bed.SendAudioHandler().HandleAsync(Command(bed, conversation.Id), Ct));

        Assert.Same(thrown, error);
        Assert.Single(bed.Store.Deleted);
    }

    // Review Focus 1, otra vez: en un reintento de un Failed el objeto es el que la fila ya sirve.
    [Fact]
    public async Task AnExceptionAfterTheR2UploadOnARetryKeepsTheAudioTheFailedRowServes()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);
        var failed = Guid.CreateVersion7();
        bed.Outbound.Existing = new ExistingOutbound(failed, MessageStatus.Failed, bed.Now.AddMinutes(-5), bed.MemberId, "", null, MessageKind.Audio, "audio/ogg");
        bed.Meta.UploadException = new OperationCanceledException();

        await Assert.ThrowsAsync<OperationCanceledException>(() => bed.SendAudioHandler().HandleAsync(Command(bed, conversation.Id), Ct));

        Assert.Empty(bed.Store.Deleted);
        Assert.Equal($"messaging/{bed.TenantId}/{failed}", Assert.Single(bed.Store.Objects).Key);
    }

    [Fact]
    public async Task AQuotedVoiceNoteOnAnUnassignedConversationTakesItAndCarriesTheContext()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);
        var quoted = Guid.CreateVersion7();
        bed.Messages.Targets[quoted] = new ReplyTargetRow(quoted, conversation.Id, MessageDirection.Inbound, MessageKind.Text, "¿Tienen?", null, "wamid.q");

        var message = await bed.SendAudioHandler().HandleAsync(Command(bed, conversation.Id, replyTo: quoted), Ct);

        Assert.Equal("wamid.q", Assert.Single(bed.Meta.AudioSends).ContextWamid);
        Assert.Equal(quoted, message.ReplyTo!.Id);
        Assert.Equal([conversation.Id], bed.Repository.AutoAssigned);
        Assert.Equal((quoted, "wamid.q"), (bed.Outbound.Claims[0].Draft.ReplyToMessageId!.Value, bed.Outbound.Claims[0].Draft.ReplyToWamid));
    }

    // Como el texto: la persona cierra la pestaña mientras Meta acepta la nota. Ya salió: la fila queda Sent.
    [Fact]
    public async Task AnAbortedRequestAfterMetaAcceptedStillCommitsSent()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);
        using var aborted = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        bed.Meta.NextSend = new SendResult(SendOutcome.Sent, "wamid.aborted", null, null);
        bed.Meta.DuringSend = aborted.Cancel;

        var message = await bed.SendAudioHandler().HandleAsync(Command(bed, conversation.Id), aborted.Token);

        Assert.Equal("Sent", message.Status);
        Assert.Equal("committed-sent:wamid.aborted", Assert.Single(bed.Outbound.Claims).Outcome);
    }
}
