using System.Security.Cryptography;
using System.Text;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-11-09, prueba 5 («Ejecución» y «Fallas de almacenamiento»), con los fallos del preflight P6 (lo que encontró el
/// reclamo) y P17 (sin acuse de lectura): el orden de <see cref="ScheduledMessageExecutor"/>. Antes del reclamo, bloqueo, ventana y conexión
/// terminan el programado sin fila de mensaje; el adjunto se copia y se sube antes del reclamo (<c>media_missing</c> si falla); después del
/// reclamo una respuesta de Meta la liquida la tabla de siempre y cualquier otra falla es <c>unknown_outcome</c>.</summary>
public sealed class ScheduledMessageExecutorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4];
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7 contenido");
    private static readonly byte[] Ogg = Encoding.ASCII.GetBytes("OggS nota de voz");

    private readonly MessagingTestBed _bed = new();
    private readonly FakeScheduledMessageStore _scheduled = new();

    /// <summary>Quien escribió el contenido: nunca el miembro del banco, para que la prueba vea de dónde sale el autor del mensaje.</summary>
    private readonly Guid _author = Guid.CreateVersion7();

    private ScheduledMessageExecutor Executor(ITenantClock? tenantClock = null) =>
        new(
            _bed.Conversations, _bed.Repository, _bed.Outbound, _bed.Meta, _bed.Instagram, _bed.Store, _bed.Staging, _bed.Compressor,
            _bed.Connections, _bed.Notifier, _bed.Receipts, _scheduled, tenantClock ?? new FakeTenantClock(new FakeClock(_bed.Now)),
            new FakeClock(_bed.Now));

    private ClaimedScheduledMessage Text(ConversationRow conversation, string text = "Hola") =>
        new(Guid.CreateVersion7(), _bed.TenantId, conversation.Id, Guid.CreateVersion7(), MessageKind.Text, false, text, null, null, null, null, null, null,
            _author, 1);

    private ClaimedScheduledMessage Media(
        ConversationRow conversation, MessageKind kind, string extension, byte[] bytes, string mimeType, string? fileName, string? caption = null,
        bool voice = false)
    {
        var id = Guid.CreateVersion7();
        var key = MessagingMediaKeys.Scheduled(_bed.TenantId, id, extension);
        _bed.Store.Objects[key] = (bytes, mimeType);
        return new ClaimedScheduledMessage(
            id, _bed.TenantId, conversation.Id, Guid.CreateVersion7(), kind, voice, null, caption, key, mimeType, fileName, bytes.Length,
            Convert.ToHexStringLower(SHA256.HashData(bytes)), _author, 1);
    }

    private ClaimedScheduledMessage Image(ConversationRow conversation, string? caption = null) =>
        Media(conversation, MessageKind.Image, ".jpg", Jpeg, "image/jpeg", "foto.jpg", caption);

    private static ExistingOutbound Row(MessageStatus status, bool retryDisabled = false) =>
        new(Guid.CreateVersion7(), status, DateTimeOffset.UnixEpoch, null, "Hola", RetryDisabled: retryDisabled);

    private string FinalKey(Guid messageId) => MessagingMediaKeys.For(_bed.TenantId, messageId);

    // --- Guardas antes del reclamo -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AMissingConversationWritesNothing()
    {
        var gone = _bed.OpenConversation(lastInboundHoursAgo: 1);
        _bed.Conversations.Rows.Clear();

        await Executor().ExecuteAsync(Text(gone), Ct);

        Assert.Empty(_scheduled.Sent);
        Assert.Empty(_scheduled.Failed);
        Assert.Empty(_bed.Outbound.Claims);
        Assert.Empty(_bed.Notifier.Events);
    }

    // La ventana se cierra justo a las 24 h (LastInboundAt + 24 h <= now), como en el envío del asesor.
    [Theory]
    [InlineData(24)]
    [InlineData(30)]
    public async Task AClosedWindowFailsWithoutClaimOrMeta(int hoursAgo)
    {
        var conversation = _bed.OpenConversation(lastInboundHoursAgo: hoursAgo);
        var message = Text(conversation);

        await Executor().ExecuteAsync(message, Ct);

        Assert.Equal(new CompletedFailed(message.Id, 1, MessagingErrorCodes.WindowClosed), Assert.Single(_scheduled.Failed));
        Assert.Empty(_bed.Outbound.Claims);
        Assert.Empty(_bed.Meta.Sends);
    }

    [Fact]
    public async Task AConversationThatNeverWroteIsAClosedWindow()
    {
        var conversation = _bed.OpenConversation(lastInboundHoursAgo: null);

        await Executor().ExecuteAsync(Text(conversation), Ct);

        Assert.Equal(MessagingErrorCodes.WindowClosed, Assert.Single(_scheduled.Failed).Code);
        Assert.Empty(_bed.Outbound.Claims);
    }

    [Fact]
    public async Task ABlockedContactFails()
    {
        var conversation = _bed.OpenConversation(lastInboundHoursAgo: 1, blocked: true);

        await Executor().ExecuteAsync(Text(conversation), Ct);

        Assert.Equal(MessagingErrorCodes.ContactBlocked, Assert.Single(_scheduled.Failed).Code);
        Assert.Empty(_bed.Outbound.Claims);
        Assert.Empty(_bed.Meta.Sends);
    }

    [Theory]
    [InlineData(ConversationChannel.WhatsApp)]
    [InlineData(ConversationChannel.Instagram)]
    public async Task APausedConnectionFails(ConversationChannel channel)
    {
        var conversation = _bed.OpenConversation(lastInboundHoursAgo: 1, channel: channel, userId: "igsid-1");
        _bed.Connections.Sender = null;
        _bed.Connections.InstagramSender = null;

        await Executor().ExecuteAsync(Text(conversation), Ct);

        Assert.Equal(MessagingErrorCodes.ConnectionUnavailable, Assert.Single(_scheduled.Failed).Code);
        Assert.Empty(_bed.Outbound.Claims);
        Assert.Empty(_bed.Meta.Sends);
        Assert.Empty(_bed.Instagram.SentTexts);
    }

    // --- Lo que ya existe con el clientId (D8b y P6) ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(MessageStatus.Sent)]
    [InlineData(MessageStatus.Delivered)]
    [InlineData(MessageStatus.Read)]
    public async Task AnAlreadySentClientIdCompletesSentWithoutMeta(MessageStatus status)
    {
        var conversation = _bed.OpenConversation(lastInboundHoursAgo: 1);
        var message = Image(conversation);
        var sent = Row(status);
        _bed.Outbound.Committed = sent;

        await Executor().ExecuteAsync(message, Ct);

        Assert.Equal(new CompletedSent(message.Id, 1, sent.Id), Assert.Single(_scheduled.Sent));
        Assert.Empty(_bed.Outbound.Claims);
        Assert.Empty(_bed.Meta.StreamUploads);
        Assert.Empty(_bed.Meta.ImageSends);
        // Sent libera el adjunto programado.
        Assert.Contains(message.MediaStorageKey!, _bed.Store.Deleted);
        Assert.Equal((_bed.TenantId, message.Id), Assert.Single(_scheduled.ClearedMediaKeys));
    }

    // Spec D8b: descartar quemó el clientId; ni la consulta sin candado ni el reclamo lo reenvían.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ADiscardedClientIdIsFinalWithoutSending(bool foundByTheClaim)
    {
        var conversation = _bed.OpenConversation(lastInboundHoursAgo: 1);
        var message = Text(conversation);
        var burned = Row(MessageStatus.Failed, retryDisabled: true);
        if (foundByTheClaim)
        {
            _bed.Outbound.Existing = burned;
        }
        else
        {
            _bed.Outbound.Committed = burned;
        }

        await Executor().ExecuteAsync(message, Ct);

        Assert.Equal(MessagingErrorCodes.MessageNotRetriable, Assert.Single(_scheduled.Failed).Code);
        Assert.Empty(_scheduled.Sent);
        Assert.Empty(_bed.Meta.Sends);
        Assert.All(_bed.Outbound.Claims, claim => Assert.Equal("rolled-back", claim.Outcome));
    }

    // P6: otra pestaña mandó el mismo clientId por el hilo entre la consulta y el reclamo. Ni se reenvía ni queda la copia del adjunto.
    [Fact]
    public async Task AClientIdSentWhileClaimingCompletesSentWithoutMeta()
    {
        var conversation = _bed.OpenConversation(lastInboundHoursAgo: 1);
        var message = Image(conversation);
        var sent = Row(MessageStatus.Sent);
        _bed.Outbound.Existing = sent;

        await Executor().ExecuteAsync(message, Ct);

        var claim = Assert.Single(_bed.Outbound.Claims);
        Assert.Equal("rolled-back", claim.Outcome);
        Assert.Equal(new CompletedSent(message.Id, 1, sent.Id), Assert.Single(_scheduled.Sent));
        Assert.Empty(_bed.Meta.ImageSends);
        Assert.Contains(FinalKey(claim.Draft.MessageId), _bed.Store.Deleted);
        Assert.False(_bed.Store.Objects.ContainsKey(FinalKey(claim.Draft.MessageId)));
    }

    // --- Envío ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ATextGoesWithTheAuthorAndTheClaimedCallback()
    {
        var conversation = _bed.OpenConversation(lastInboundHoursAgo: 1);
        var message = Text(conversation, "Hola Laura");
        _bed.Meta.NextSend = new SendResult(SendOutcome.Sent, "wamid.out", null, null);

        await Executor().ExecuteAsync(message, Ct);

        var claim = Assert.Single(_bed.Outbound.Claims);
        Assert.Equal("committed-sent:wamid.out", claim.Outcome);
        Assert.Equal(message.ClientId, claim.Draft.ClientId);
        Assert.Equal(_author, claim.Draft.SentByMemberId);
        Assert.Equal(conversation.ConnectionId, claim.Draft.ConnectionId);
        Assert.Equal(MessageKind.Text, claim.Draft.Kind);
        var sent = Assert.Single(_bed.Meta.Sends);
        Assert.Equal("Hola Laura", sent.Body);
        Assert.Equal(SendMessageHandler.CallbackPrefix + claim.MessageId.ToString("D"), sent.CallbackData);
        Assert.Null(sent.ContextWamid);
        Assert.Equal(new CompletedSent(message.Id, 1, claim.MessageId), Assert.Single(_scheduled.Sent));
        // El de la liquidación del mensaje y el del cierre del programado.
        Assert.Equal(2, _bed.Notifier.Events.Count(e => e.ConversationId == conversation.Id));
    }

    [Fact]
    public async Task MetaRejectingLeavesBothFailed()
    {
        var conversation = _bed.OpenConversation(lastInboundHoursAgo: 1);
        _bed.Meta.NextSend = new SendResult(SendOutcome.GraphError, null, 131026, "Message undeliverable");

        await Executor().ExecuteAsync(Text(conversation), Ct);

        Assert.Equal("committed-failed:131026", Assert.Single(_bed.Outbound.Claims).Outcome);
        Assert.Equal(MessagingErrorCodes.MessageRejected, Assert.Single(_scheduled.Failed).Code);
        Assert.Empty(_scheduled.Sent);
    }

    // §8.3: Meta rechazó la credencial al enviar; la fila nueva se deshace, la conexión queda marcada y el programado dice por qué.
    [Fact]
    public async Task ACredentialRejectionWhileSendingIsConnectionUnavailable()
    {
        var conversation = _bed.OpenConversation(lastInboundHoursAgo: 1);
        _bed.Meta.NextSend = new SendResult(SendOutcome.GraphError, null, MetaErrorCodes.TokenExpired, null);

        await Executor().ExecuteAsync(Text(conversation), Ct);

        Assert.Equal("rolled-back", Assert.Single(_bed.Outbound.Claims).Outcome);
        Assert.Single(_bed.Connections.Reported);
        Assert.Equal(MessagingErrorCodes.ConnectionUnavailable, Assert.Single(_scheduled.Failed).Code);
    }

    [Theory]
    [InlineData(6, "Buenos días")]
    [InlineData(13, "Buenas tardes")]
    public async Task TheGreetingUsesTheTenantClock(int localHour, string greeting)
    {
        var conversation = _bed.OpenConversation(lastInboundHoursAgo: 1);

        await Executor(new LocalHourClock(localHour)).ExecuteAsync(Text(conversation, "{saludo}, Laura"), Ct);

        Assert.Equal($"{greeting}, Laura", Assert.Single(_bed.Meta.Sends).Body);
        Assert.Equal($"{greeting}, Laura", Assert.Single(_bed.Outbound.Claims).Draft.Text);
    }

    [Fact]
    public async Task TheGreetingInACaptionIsResolvedToo()
    {
        var conversation = _bed.OpenConversation(lastInboundHoursAgo: 1);

        await Executor(new LocalHourClock(20)).ExecuteAsync(Image(conversation, "{saludo}, tu foto"), Ct);

        Assert.Equal("Buenas noches, tu foto", Assert.Single(_bed.Meta.ImageSends).Caption);
        Assert.Equal("Buenas noches, tu foto", Assert.Single(_bed.Outbound.Claims).Draft.Caption);
    }

    // Spec D4 y X4/X7: resuelta, de otro asesor y con el asistente activo sale igual, sin reabrir, asignar ni autoasignar; liquida con seen 0.
    [Fact]
    public async Task AResolvedOtherAssigneeAndActiveAssistantStillSendWithoutChangingState()
    {
        var conversation = _bed.OpenConversation(
            lastInboundHoursAgo: 1, resolved: true, assignedTo: Guid.CreateVersion7(), automation: ConversationAutomationStatus.Active);

        await Executor().ExecuteAsync(Text(conversation), Ct);

        Assert.Single(_bed.Meta.Sends);
        Assert.Single(_scheduled.Sent);
        Assert.Empty(_bed.Repository.AutoAssigned);
        Assert.Equal((conversation.Id, 0), Assert.Single(_bed.Repository.Replied));
    }

    // P17: un programado no manda el acuse de lectura, ni siquiera si el contador quedó en 0.
    [Theory]
    [InlineData(ConversationChannel.WhatsApp)]
    [InlineData(ConversationChannel.Instagram)]
    public async Task NoReadReceiptIsSent(ConversationChannel channel)
    {
        var conversation = _bed.OpenConversation(lastInboundHoursAgo: 1, channel: channel, userId: "igsid-1", lastInboundWamid: "wamid.in");
        _bed.Repository.NextReplied = new RepliedTarget(
            new ReadReceiptTarget(_bed.ConnectionId, "wamid.in", _bed.Now.AddHours(-1), channel, "igsid-1"), RemainingUnread: 0);

        await Executor().ExecuteAsync(Text(conversation), Ct);

        Assert.Single(_scheduled.Sent);
        Assert.Empty(_bed.Meta.ReadReceipts);
        Assert.Empty(_bed.Instagram.Seen);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AVoiceNoteGoesWithVoiceTrue(bool voice)
    {
        var conversation = _bed.OpenConversation(lastInboundHoursAgo: 1);
        var message = Media(conversation, MessageKind.Audio, ".ogg", Ogg, "audio/ogg", voice ? null : "audio.ogg", voice: voice);

        await Executor().ExecuteAsync(message, Ct);

        Assert.Equal(voice, Assert.Single(_bed.Meta.AudioSends).Voice);
        Assert.Single(_scheduled.Sent);
    }

    // Task 4: la subida a Meta lleva el tamaño y el mime guardados (la imagen ya comprimida), sin volver a comprimir.
    [Fact]
    public async Task AnImageUploadsTheStoredBytesWithoutCompressing()
    {
        var conversation = _bed.OpenConversation(lastInboundHoursAgo: 1);
        var message = Image(conversation, "tu foto");

        await Executor().ExecuteAsync(message, Ct);

        var upload = Assert.Single(_bed.Meta.StreamUploads);
        Assert.Equal(Jpeg, upload.Content);
        Assert.Equal(Jpeg.Length, upload.DeclaredLength);
        Assert.Equal("image/jpeg", upload.MimeType);
        Assert.Empty(_bed.Compressor.Received);
        var image = Assert.Single(_bed.Meta.ImageSends);
        Assert.Equal("media.default", image.MediaId);
        Assert.Equal("tu foto", image.Caption);
        var claim = Assert.Single(_bed.Outbound.Claims);
        Assert.Equal("media.default", claim.MetaMediaId);
        Assert.Equal(new OutboundMedia("image/jpeg", Jpeg.Length, message.MediaSha256, "foto.jpg"), claim.Draft.Media);
        Assert.Equal(MessageKind.Image, claim.Draft.Kind);
        Assert.Null(claim.Draft.Text);
    }

    [Fact]
    public async Task ADocumentGoesWithItsFileNameAndCaption()
    {
        var conversation = _bed.OpenConversation(lastInboundHoursAgo: 1);
        var message = Media(conversation, MessageKind.Document, ".pdf", Pdf, "application/pdf", "cotizacion.pdf", "la cotización");

        await Executor().ExecuteAsync(message, Ct);

        var document = Assert.Single(_bed.Meta.DocumentSends);
        Assert.Equal("cotizacion.pdf", document.FileName);
        Assert.Equal("la cotización", document.Caption);
        Assert.Equal("application/pdf", Assert.Single(_bed.Meta.StreamUploads).MimeType);
    }

    // Sent libera el objeto programado; la copia de la clave final es la que sirve la fila del mensaje y se queda.
    [Fact]
    public async Task ASentAttachmentFreesTheScheduledObjectAndKeepsTheFinalCopy()
    {
        var conversation = _bed.OpenConversation(lastInboundHoursAgo: 1);
        var message = Image(conversation);

        await Executor().ExecuteAsync(message, Ct);

        var claim = Assert.Single(_bed.Outbound.Claims);
        Assert.False(_bed.Store.Objects.ContainsKey(message.MediaStorageKey!));
        Assert.Equal(Jpeg, _bed.Store.Objects[FinalKey(claim.MessageId)].Bytes);
        Assert.Equal((_bed.TenantId, message.Id), Assert.Single(_scheduled.ClearedMediaKeys));
    }

    [Fact]
    public async Task AFailedDeleteOfTheScheduledObjectKeepsItsKeyForThePurge()
    {
        var conversation = _bed.OpenConversation(lastInboundHoursAgo: 1);
        var message = Image(conversation);
        _bed.Store.DeleteFailure = new InvalidOperationException("R2 no responde");

        await Executor().ExecuteAsync(message, Ct);

        Assert.Single(_scheduled.Sent);
        Assert.Empty(_scheduled.ClearedMediaKeys);
    }

    [Fact]
    public async Task AnInstagramAttachmentGoesBySignedUrlOfTheFinalKeyWithoutCaption()
    {
        var conversation = _bed.OpenConversation(lastInboundHoursAgo: 1, channel: ConversationChannel.Instagram, userId: "igsid-1");
        var message = Image(conversation, "no viaja");

        await Executor().ExecuteAsync(message, Ct);

        var claim = Assert.Single(_bed.Outbound.Claims);
        Assert.Equal((FinalKey(claim.MessageId), SendMediaMessageHandler.InstagramDownloadLifetime), Assert.Single(_bed.Store.DownloadUrls));
        var sent = Assert.Single(_bed.Instagram.SentAttachments);
        Assert.Equal("image", sent.Type);
        Assert.Equal("igsid-1", sent.RecipientId);
        Assert.Empty(_bed.Meta.StreamUploads);
        Assert.Equal("no viaja", claim.Draft.Caption);
        Assert.Single(_scheduled.Sent);
    }

    [Fact]
    public async Task AnInstagramVoiceNoteGoesAsAudio()
    {
        var conversation = _bed.OpenConversation(lastInboundHoursAgo: 1, channel: ConversationChannel.Instagram, userId: "igsid-1");
        var message = Media(conversation, MessageKind.Audio, ".wav", Ogg, "audio/wav", null, voice: true);

        await Executor().ExecuteAsync(message, Ct);

        Assert.Equal(InstagramAudio.AttachmentType, Assert.Single(_bed.Instagram.SentAttachments).Type);
    }

    [Fact]
    public async Task AnInstagramTextGoesToTheUser()
    {
        var conversation = _bed.OpenConversation(lastInboundHoursAgo: 1, channel: ConversationChannel.Instagram, userId: "igsid-1");

        await Executor().ExecuteAsync(Text(conversation, "Hola"), Ct);

        var sent = Assert.Single(_bed.Instagram.SentTexts);
        Assert.Equal(("igsid-1", "Hola"), (sent.RecipientId, sent.Text));
        Assert.Single(_scheduled.Sent);
    }

    // --- Reintento (mismo clientId) ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ARetryReusesTheFailedRowAndItsFinalKey()
    {
        var conversation = _bed.OpenConversation(lastInboundHoursAgo: 1);
        var message = Image(conversation);
        var failed = Row(MessageStatus.Failed);
        _bed.Outbound.Committed = failed;
        _bed.Outbound.Existing = failed;

        await Executor().ExecuteAsync(message, Ct);

        var claim = Assert.Single(_bed.Outbound.Claims);
        Assert.Equal(failed.Id, claim.Draft.MessageId);
        Assert.Equal("committed-sent:wamid.default", claim.Outcome);
        Assert.Equal(FinalKey(failed.Id), Assert.Single(_bed.Staging.Promoted).FinalKey);
        Assert.Equal(new CompletedSent(message.Id, 1, failed.Id), Assert.Single(_scheduled.Sent));
    }

    // El lease hace del ejecutor el único escritor, pero si el reclamo devuelve otro id se borra la copia y se rehace con ése.
    [Fact]
    public async Task AFailedRowFoundOnlyByTheClaimMovesTheCopyToItsKey()
    {
        var conversation = _bed.OpenConversation(lastInboundHoursAgo: 1);
        var message = Image(conversation);
        var failed = Row(MessageStatus.Failed);
        _bed.Outbound.Existing = failed;

        await Executor().ExecuteAsync(message, Ct);

        var claim = Assert.Single(_bed.Outbound.Claims);
        Assert.NotEqual(failed.Id, claim.Draft.MessageId);
        Assert.Equal([FinalKey(claim.Draft.MessageId), FinalKey(failed.Id)], _bed.Staging.Promoted.Select(p => p.FinalKey));
        Assert.Contains(FinalKey(claim.Draft.MessageId), _bed.Store.Deleted);
        Assert.True(_bed.Store.Objects.ContainsKey(FinalKey(failed.Id)));
        Assert.Equal(SendMessageHandler.CallbackPrefix + failed.Id.ToString("D"), Assert.Single(_bed.Meta.ImageSends).CallbackData);
        Assert.Equal(new CompletedSent(message.Id, 1, failed.Id), Assert.Single(_scheduled.Sent));
    }

    // --- Almacenamiento antes del reclamo ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheCopyToTheFinalKeyHappensBeforeTheClaim()
    {
        var conversation = _bed.OpenConversation(lastInboundHoursAgo: 1);
        var (promotedAtUpload, claimsAtUpload) = (-1, -1);
        _bed.Meta.DuringStreamUpload = () => (promotedAtUpload, claimsAtUpload) = (_bed.Staging.Promoted.Count, _bed.Outbound.Claims.Count);

        await Executor().ExecuteAsync(Image(conversation), Ct);

        Assert.Equal((1, 0), (promotedAtUpload, claimsAtUpload));
        Assert.Single(_bed.Outbound.Claims);
    }

    public enum StorageFailure
    {
        MissingObject,
        PurgedKey,
        FailingPromote,
        FailingMetaUpload,
        ThrowingMetaUpload,
    }

    [Theory]
    [InlineData(StorageFailure.MissingObject)]
    [InlineData(StorageFailure.PurgedKey)]
    [InlineData(StorageFailure.FailingPromote)]
    [InlineData(StorageFailure.FailingMetaUpload)]
    [InlineData(StorageFailure.ThrowingMetaUpload)]
    public async Task AStorageFailureBeforeTheClaimIsMediaMissing(StorageFailure failure)
    {
        var conversation = _bed.OpenConversation(lastInboundHoursAgo: 1);
        var message = Image(conversation);
        switch (failure)
        {
            case StorageFailure.MissingObject:
                _bed.Store.Objects.Remove(message.MediaStorageKey!);
                break;
            case StorageFailure.PurgedKey:
                message = message with { MediaStorageKey = null };
                break;
            case StorageFailure.FailingPromote:
                _bed.Staging.PromoteFailure = new InvalidOperationException("R2 no copió");
                break;
            case StorageFailure.FailingMetaUpload:
                _bed.Meta.NextUpload = new MessagingGraphResult<string>(null, new MessagingGraphFailure(503, null, "http_503"));
                break;
            case StorageFailure.ThrowingMetaUpload:
                _bed.Meta.UploadException = new HttpRequestException("Meta no responde");
                break;
        }

        await Executor().ExecuteAsync(message, Ct);

        Assert.Equal(new CompletedFailed(message.Id, 1, MessagingErrorCodes.ScheduledMediaMissing), Assert.Single(_scheduled.Failed));
        Assert.Empty(_bed.Outbound.Claims);
        Assert.Empty(_bed.Meta.ImageSends);
        Assert.Empty(_bed.Connections.Reported);
        // Una copia parcial en la clave final no queda: ninguna fila la sirve.
        Assert.DoesNotContain(_bed.Store.Objects.Keys, key => key.StartsWith($"messaging/{_bed.TenantId}/", StringComparison.Ordinal));
    }

    // Spec «Fallas de almacenamiento»: en un reintento la clave final es la que sirve la fila Failed; una subida fallida no la borra.
    [Fact]
    public async Task AFailedUploadOnARetryKeepsTheFinalKeyOfTheFailedRow()
    {
        var conversation = _bed.OpenConversation(lastInboundHoursAgo: 1);
        var message = Image(conversation);
        var failed = Row(MessageStatus.Failed);
        _bed.Outbound.Committed = failed;
        _bed.Meta.NextUpload = new MessagingGraphResult<string>(null, new MessagingGraphFailure(503, null, "http_503"));

        await Executor().ExecuteAsync(message, Ct);

        Assert.Equal(MessagingErrorCodes.ScheduledMediaMissing, Assert.Single(_scheduled.Failed).Code);
        Assert.DoesNotContain(FinalKey(failed.Id), _bed.Store.Deleted);
    }

    [Theory]
    [InlineData(190)]
    [InlineData(133010)]
    public async Task AnUploadRejectedForCredentialsFlagsTheConnectionAndIsMediaMissing(int code)
    {
        var conversation = _bed.OpenConversation(lastInboundHoursAgo: 1);
        _bed.Meta.NextUpload = new MessagingGraphResult<string>(null, new MessagingGraphFailure(401, code, "http_401"));

        await Executor().ExecuteAsync(Image(conversation), Ct);

        Assert.Equal(
            new RejectedReport(_bed.TenantId, conversation.ConnectionId, MetaErrorCodes.RejectedReason(code)), Assert.Single(_bed.Connections.Reported));
        Assert.Equal(MessagingErrorCodes.ScheduledMediaMissing, Assert.Single(_scheduled.Failed).Code);
        Assert.Empty(_bed.Outbound.Claims);
    }

    [Fact]
    public async Task AFailedConnectionFlagStillEndsMediaMissing()
    {
        var conversation = _bed.OpenConversation(lastInboundHoursAgo: 1);
        _bed.Meta.NextUpload = new MessagingGraphResult<string>(null, new MessagingGraphFailure(401, MetaErrorCodes.TokenExpired, "http_401"));
        _bed.Connections.ReportFailure = new InvalidOperationException("Integrations no guardó");

        await Executor().ExecuteAsync(Image(conversation), Ct);

        Assert.Equal(MessagingErrorCodes.ScheduledMediaMissing, Assert.Single(_scheduled.Failed).Code);
    }

    // --- Fallas después del reclamo ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnExceptionAfterTheClaimIsUnknownOutcomeAndANewRowIsRolledBack()
    {
        var conversation = _bed.OpenConversation(lastInboundHoursAgo: 1);
        var message = Image(conversation);
        _bed.Meta.DuringSend = () => throw new HttpRequestException("se cayó la conexión");

        await Executor().ExecuteAsync(message, Ct);

        var claim = Assert.Single(_bed.Outbound.Claims);
        Assert.Equal("rolled-back", claim.Outcome);
        Assert.Contains(FinalKey(claim.MessageId), _bed.Store.Deleted);
        Assert.Equal(new CompletedFailed(message.Id, 1, MessagingErrorCodes.ScheduledUnknownOutcome), Assert.Single(_scheduled.Failed));
        // Sólo Sent libera el objeto programado: un reintento lo necesita.
        Assert.True(_bed.Store.Objects.ContainsKey(message.MediaStorageKey!));
    }

    [Fact]
    public async Task AnExceptionAfterTheClaimLeavesAReusedFailedRowIntact()
    {
        var conversation = _bed.OpenConversation(lastInboundHoursAgo: 1);
        var message = Image(conversation);
        var failed = Row(MessageStatus.Failed);
        _bed.Outbound.Committed = failed;
        _bed.Outbound.Existing = failed;
        _bed.Meta.DuringSend = () => throw new HttpRequestException("se cayó la conexión");

        await Executor().ExecuteAsync(message, Ct);

        Assert.Equal("rolled-back", Assert.Single(_bed.Outbound.Claims).Outcome);
        Assert.DoesNotContain(FinalKey(failed.Id), _bed.Store.Deleted);
        Assert.Equal(MessagingErrorCodes.ScheduledUnknownOutcome, Assert.Single(_scheduled.Failed).Code);
    }

    [Fact]
    public async Task AnUnsignableInstagramUrlIsUnknownOutcome()
    {
        var conversation = _bed.OpenConversation(lastInboundHoursAgo: 1, channel: ConversationChannel.Instagram, userId: "igsid-1");
        _bed.Store.DownloadUrlFailure = new InvalidOperationException("R2 no firma");

        await Executor().ExecuteAsync(Image(conversation), Ct);

        Assert.Equal("rolled-back", Assert.Single(_bed.Outbound.Claims).Outcome);
        Assert.Empty(_bed.Instagram.SentAttachments);
        Assert.Equal(MessagingErrorCodes.ScheduledUnknownOutcome, Assert.Single(_scheduled.Failed).Code);
    }

    // --- Cierre --------------------------------------------------------------------------------------------------------------------

    // El CAS no tocó filas (otro recuperó el lease): no se avisa ni se libera el objeto, que ahora es de quien lo tiene.
    [Fact]
    public async Task ALostLeaseWritesNothingAfterTheSend()
    {
        var conversation = _bed.OpenConversation(lastInboundHoursAgo: 1);
        var message = Image(conversation);
        _scheduled.CompleteSucceeds = false;

        await Executor().ExecuteAsync(message, Ct);

        Assert.True(_bed.Store.Objects.ContainsKey(message.MediaStorageKey!));
        Assert.Empty(_scheduled.ClearedMediaKeys);
        // Sólo el aviso de la liquidación del mensaje; el del programado sale únicamente tras un cierre que escribió.
        Assert.Single(_bed.Notifier.Events);
    }

    [Fact]
    public async Task AFailedNotificationDoesNotChangeTheOutcome()
    {
        var conversation = _bed.OpenConversation(lastInboundHoursAgo: 30);
        _bed.Notifier.Fail = new InvalidOperationException("pg_notify caído");

        await Executor().ExecuteAsync(Text(conversation), Ct);

        Assert.Equal(MessagingErrorCodes.WindowClosed, Assert.Single(_scheduled.Failed).Code);
    }

    [Fact]
    public async Task APreClaimFailureNotifiesTheConversation()
    {
        var conversation = _bed.OpenConversation(lastInboundHoursAgo: 30);

        await Executor().ExecuteAsync(Text(conversation), Ct);

        Assert.Equal(conversation.Id, Assert.Single(_bed.Notifier.Events).ConversationId);
    }

    /// <summary>Bogotá (UTC-5): la hora local nunca coincide con la del instante en UTC.</summary>
    private sealed class LocalHourClock(int localHour) : ITenantClock
    {
        public Task<TenantCalendar> GetAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult(new TenantCalendar(
                new DateTimeOffset(2026, 10, 9, localHour, 0, 0, TimeSpan.FromHours(-5)),
                TimeZoneInfo.FindSystemTimeZoneById("America/Bogota")));
    }
}
