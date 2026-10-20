using System.Security.Cryptography;
using System.Text;
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Messaging-media-reply R3 a R7, R10 y R13 a R15: el orden de <see cref="SendMediaMessageHandler"/>. Todo lo lento (leer
/// R2, sniff, comprimir, subir a Meta) va antes del reclamo; dentro quedan la escritura en R2 y el envío. Failed conserva el
/// staging (S4 del design); Sent lo borra.</summary>
public sealed class SendMediaMessageHandlerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5, 6];
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7 contenido");
    private static readonly byte[] Zip = [0x50, 0x4B, 0x03, 0x04, 1, 2, 3, 4, 5];
    private static readonly byte[] Ole = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 7, 7];
    private static readonly byte[] Exe = [0x4D, 0x5A, 0x90, 0x00, 3, 0, 0, 0, 4];

    // Spec 2026-10-20: cabeceras reales, como las del sniffer, con algo de cuerpo detrás.
    private static readonly byte[] Mp4 = [0, 0, 0, 0x18, .. "ftypmp42"u8, 0, 0, 0, 0, .. "isommp42"u8, 0, 0, 0, 8, .. "free"u8, .. new byte[40]];
    private static readonly byte[] M4a = [0, 0, 0, 0x18, .. "ftypM4A "u8, 0, 0, 2, 0, .. "isomM4A "u8, 0, 0, 0, 8, .. "free"u8, .. new byte[40]];
    private static readonly byte[] Mp3 = [.. "ID3"u8, 4, 0, 0, 0, 0, 0, 0, 0xFF, 0xFB, 0x90, 0x64, .. new byte[60]];

    private sealed record Staged(ConversationRow Conversation, Guid UploadId, string StagingKey);

    private static Staged Prepare(MessagingTestBed bed, string fileName, byte[] bytes, bool assigned = true)
    {
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1, assignedTo: assigned ? bed.MemberId : null);
        var uploadId = Guid.CreateVersion7();
        return new Staged(conversation, uploadId, bed.Stage(conversation.Id, uploadId, fileName, bytes));
    }

    private static SendMediaMessageCommand Command(MessagingTestBed bed, Staged staged, string fileName, string? caption = null, Guid? replyTo = null) =>
        new(bed.TenantId, staged.Conversation.Id, bed.ClientId, staged.UploadId, fileName, caption, replyTo);

    private static string FinalKeyOf(MessagingTestBed bed, FakeClaim claim) => MessagingMediaKeys.For(bed.TenantId, claim.MessageId);

    // S5, S7: la comprimida es lo que viaja y lo que se guarda; un PNG se guarda como JPEG con el nombre base y .jpg.
    [Fact]
    public async Task AnImageIsCompressedUploadedToMetaStoredAsJpegSentAndItsStagingDeleted()
    {
        var bed = new MessagingTestBed();
        var staged = Prepare(bed, "Foto.PNG", Png);
        bed.Meta.NextUpload = new MessagingGraphResult<string>("media.img", null);
        bed.Meta.NextSend = new SendResult(SendOutcome.Sent, "wamid.img", null, null);

        var message = await bed.SendMediaHandler().HandleAsync(Command(bed, staged, "Foto.PNG", "Mira esto"), Ct);

        Assert.Equal(("Image", "Sent", (string?)null, bed.ClientId), (message.Kind, message.Status, message.Text, message.ClientId));
        Assert.Equal(new MediaDto($"/api/v1/tenants/{bed.TenantId}/messaging/media/{message.Id}", "image/jpeg", "Foto.jpg", "Mira esto"), message.Media);
        Assert.Equal(Png, Assert.Single(bed.Compressor.Received));
        var upload = Assert.Single(bed.Meta.StreamUploads);
        Assert.Equal(FakeImageCompressor.Output, upload.Content);
        Assert.Equal(((long)FakeImageCompressor.Output.Length, "image/jpeg", "image.jpg"), (upload.DeclaredLength, upload.MimeType, upload.FileName));
        var send = Assert.Single(bed.Meta.ImageSends);
        Assert.Equal(("media.img", "Mira esto", $"qep:{message.Id}", (string?)null), (send.MediaId, send.Caption, send.CallbackData, send.ContextWamid));
        Assert.Equal((SendTarget)new SendToPhone(staged.Conversation.WaId!), send.Target);
        var claim = Assert.Single(bed.Outbound.Claims);
        Assert.Equal(("committed-sent:wamid.img", "media.img"), (claim.Outcome, claim.MetaMediaId));
        Assert.Equal((MessageKind.Image, "Mira esto", (string?)null), (claim.Draft.Kind, claim.Draft.Caption, claim.Draft.Text));
        Assert.Equal(new OutboundMedia("image/jpeg", FakeImageCompressor.Output.Length, Convert.ToHexStringLower(SHA256.HashData(FakeImageCompressor.Output)), "Foto.jpg"), claim.Draft.Media);
        // Sólo persiste el objeto final, con la comprimida; el original (staging) se borró.
        var stored = Assert.Single(bed.Store.Objects);
        Assert.Equal((FinalKeyOf(bed, claim), "image/jpeg"), (stored.Key, stored.Value.ContentType));
        Assert.Equal(FakeImageCompressor.Output, stored.Value.Bytes);
        Assert.Equal([staged.StagingKey], bed.Store.Deleted);
    }

    // R4 y el design: la subida a Meta no deja nada persistido, así que va antes del reclamo; el staging se lee una sola vez.
    [Fact]
    public async Task TheSlowWorkHappensBeforeTheClaimAndTheStagingIsReadOnce()
    {
        var bed = new MessagingTestBed();
        var staged = Prepare(bed, "a.pdf", Pdf);
        var claimsWhenMetaReceivedTheFile = -1;
        bed.Meta.DuringStreamUpload = () => claimsWhenMetaReceivedTheFile = bed.Outbound.Claims.Count;
        var claimsWhenMetaSent = -1;
        bed.Meta.DuringSend = () => claimsWhenMetaSent = bed.Outbound.Claims.Count;

        await bed.SendMediaHandler().HandleAsync(Command(bed, staged, "a.pdf"), Ct);

        Assert.Equal((0, 1), (claimsWhenMetaReceivedTheFile, claimsWhenMetaSent));
        Assert.Equal([staged.StagingKey], bed.Store.Opened);
        Assert.Equal([staged.StagingKey], bed.Staging.Stats);
    }

    // S8, S14, S9: el archivo viaja intacto con nombre genérico; el real va en document.filename; el hash sale del mismo stream.
    [Fact]
    public async Task ADocumentTravelsIntactWithAGenericMultipartNameItsRealNameInThePayloadAndTheQuotedContext()
    {
        var bed = new MessagingTestBed();
        var staged = Prepare(bed, "Cotización mayo.xlsx", Zip);
        var quoted = Guid.CreateVersion7();
        bed.Messages.Targets[quoted] = new ReplyTargetRow(quoted, staged.Conversation.Id, MessageDirection.Inbound, MessageKind.Text, "¿Tienen?", null, "wamid.q");
        bed.Meta.NextUpload = new MessagingGraphResult<string>("media.doc", null);
        bed.Meta.NextSend = new SendResult(SendOutcome.Sent, "wamid.doc", null, null);

        var message = await bed.SendMediaHandler().HandleAsync(Command(bed, staged, "Cotización mayo.xlsx", "Cotización", quoted), Ct);

        Assert.Equal(("Document", "Sent"), (message.Kind, message.Status));
        Assert.Equal(new MediaDto($"/api/v1/tenants/{bed.TenantId}/messaging/media/{message.Id}", Mime.Xlsx, "Cotización mayo.xlsx", "Cotización"), message.Media);
        Assert.Equal(quoted, message.ReplyTo?.Id);
        var upload = Assert.Single(bed.Meta.StreamUploads);
        Assert.Equal(Zip, upload.Content);
        Assert.Equal(((long)Zip.Length, Mime.Xlsx, "document.xlsx"), (upload.DeclaredLength, upload.MimeType, upload.FileName));
        var send = Assert.Single(bed.Meta.DocumentSends);
        Assert.Equal(("media.doc", "Cotización mayo.xlsx", "Cotización", "wamid.q"), (send.MediaId, send.FileName, send.Caption, send.ContextWamid));
        var claim = Assert.Single(bed.Outbound.Claims);
        Assert.Equal(new OutboundMedia(Mime.Xlsx, Zip.Length, Convert.ToHexStringLower(SHA256.HashData(Zip)), "Cotización mayo.xlsx"), claim.Draft.Media);
        Assert.Equal((quoted, "wamid.q"), (claim.Draft.ReplyToMessageId, claim.Draft.ReplyToWamid));
        // Cero bytes por la API para el final: se promueve el objeto del staging con su ETag, y el staging se borra.
        Assert.Equal((staged.StagingKey, FinalKeyOf(bed, claim), FakeUploadStaging.ETag), Assert.Single(bed.Staging.Promoted));
        Assert.Equal([staged.StagingKey], bed.Store.Deleted);
        Assert.Equal(FinalKeyOf(bed, claim), Assert.Single(bed.Store.Objects).Key);
    }

    private static class Mime
    {
        public const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    }

    [Theory]
    [InlineData("a.pdf", "application/pdf")]
    [InlineData("a.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData("a.xlsx", Mime.Xlsx)]
    [InlineData("a.doc", "application/msword")]
    [InlineData("a.xls", "application/vnd.ms-excel")]
    public async Task EveryAllowedDocumentWithItsOwnSignatureIsSent(string fileName, string mimeType)
    {
        var bed = new MessagingTestBed();
        var bytes = fileName switch { "a.pdf" => Pdf, "a.doc" or "a.xls" => Ole, _ => Zip };
        var staged = Prepare(bed, fileName, bytes);

        var message = await bed.SendMediaHandler().HandleAsync(Command(bed, staged, fileName), Ct);

        Assert.Equal(("Document", mimeType, (string?)null), (message.Kind, message.Media!.MimeType, message.Media.Caption));
        Assert.Equal(mimeType, Assert.Single(bed.Meta.StreamUploads).MimeType);
    }

    // S15 y R5: repetir el clientId devuelve la burbuja guardada con su nombre y su pie, sin tocar R2 ni Meta.
    [Fact]
    public async Task ARepeatedClientIdReturnsTheStoredBubbleWithFileNameAndCaptionWithoutTouchingR2OrMeta()
    {
        var bed = new MessagingTestBed();
        var staged = Prepare(bed, "b.pdf", Pdf);
        bed.Outbound.Committed = new ExistingOutbound(
            Guid.CreateVersion7(), MessageStatus.Sent, bed.Now, bed.MemberId, string.Empty, null, MessageKind.Document, "application/pdf", null, "a.pdf", "pie");

        var message = await bed.SendMediaHandler().HandleAsync(Command(bed, staged, "b.pdf", "otro"), Ct);

        Assert.Equal(("Document", "a.pdf", "pie"), (message.Kind, message.Media!.FileName, message.Media.Caption));
        Assert.Empty(bed.Staging.Stats);
        Assert.Empty(bed.Store.Opened);
        Assert.Empty(bed.Meta.StreamUploads);
        Assert.Empty(bed.Outbound.Claims);
    }

    // S20 con el design: el staging se consumió con otro clientId → 422; pero si en el entretiempo terminó el mismo clientId, gana la idempotencia.
    [Fact]
    public async Task AConsumedStagingIsNotFoundForAnotherClientIdButTheSameClientIdThatJustFinishedWins()
    {
        var bed = new MessagingTestBed();
        var staged = Prepare(bed, "a.pdf", Pdf);
        bed.Store.Objects.Remove(staged.StagingKey);

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => bed.SendMediaHandler().HandleAsync(Command(bed, staged, "a.pdf"), Ct));
        Assert.Equal(MessagingErrorCodes.MediaUploadNotFound, error.Code);
        Assert.Empty(bed.Outbound.Claims);

        bed.Outbound.CommittedOnSecondLookup = new ExistingOutbound(
            Guid.CreateVersion7(), MessageStatus.Sent, bed.Now, bed.MemberId, string.Empty, null, MessageKind.Document, "application/pdf", null, "a.pdf", null);
        var message = await bed.SendMediaHandler().HandleAsync(Command(bed, staged, "a.pdf"), Ct);

        Assert.Equal("a.pdf", message.Media!.FileName);
        Assert.Empty(bed.Meta.StreamUploads);
    }

    // S19 y S21: la clave lleva tenant, conversación y miembro del contexto; un uploadId de otro miembro (o inexistente) no resuelve.
    [Fact]
    public async Task AnUploadOfAnotherMemberOrAnUnknownOneDoesNotResolve()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1, assignedTo: bed.MemberId);
        var otherMembers = Guid.CreateVersion7();
        var theirKey = bed.Stage(conversation.Id, otherMembers, "a.pdf", Pdf, memberId: Guid.CreateVersion7());

        foreach (var uploadId in new[] { otherMembers, Guid.CreateVersion7() })
        {
            var error = await Assert.ThrowsAsync<MessagingDomainException>(() =>
                bed.SendMediaHandler().HandleAsync(new SendMediaMessageCommand(bed.TenantId, conversation.Id, bed.ClientId, uploadId, "a.pdf", null), Ct));
            Assert.Equal(MessagingErrorCodes.MediaUploadNotFound, error.Code);
        }

        Assert.True(bed.Store.Objects.ContainsKey(theirKey));
        Assert.Empty(bed.Meta.StreamUploads);
        Assert.Empty(bed.Outbound.Claims);
    }

    // S12: el PUT prefirmado no impone tope; el stat manda y el objeto se borra.
    [Theory]
    [InlineData("a.jpg", 25L * 1024 * 1024 + 1)]
    [InlineData("a.pdf", 100L * 1024 * 1024 + 1)]
    [InlineData("a.pdf", 0L)]
    public async Task AnObjectOverTheLimitOrEmptyIsTooLargeAndIsDeletedWithoutReadingItOrCallingMeta(string fileName, long statSize)
    {
        var bed = new MessagingTestBed();
        var staged = Prepare(bed, fileName, fileName == "a.jpg" ? Jpeg : Pdf);
        bed.Staging.StatSize = statSize;

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => bed.SendMediaHandler().HandleAsync(Command(bed, staged, fileName), Ct));

        Assert.Equal(MessagingErrorCodes.MediaTooLarge, error.Code);
        Assert.Equal([staged.StagingKey], bed.Store.Deleted);
        Assert.Empty(bed.Store.Opened);
        Assert.Empty(bed.Meta.StreamUploads);
        Assert.Empty(bed.Outbound.Claims);
    }

    // S10, S11: el contenido manda sobre el nombre; se borra el staging y nada llega a Meta.
    [Theory]
    [InlineData("virus.pdf", "exe")]
    [InlineData("a.xlsx", "ole")]
    [InlineData("a.xls", "zip")]
    [InlineData("a.jpg", "exe")]
    public async Task ContentThatDoesNotMatchTheExtensionIsUnsupportedAndTheStagingIsDeleted(string fileName, string content)
    {
        var bed = new MessagingTestBed();
        var staged = Prepare(bed, fileName, content switch { "exe" => Exe, "ole" => Ole, _ => Zip });

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => bed.SendMediaHandler().HandleAsync(Command(bed, staged, fileName), Ct));

        Assert.Equal(MessagingErrorCodes.MediaUnsupportedFormat, error.Code);
        Assert.Equal([staged.StagingKey], bed.Store.Deleted);
        Assert.Empty(bed.Store.Objects);
        Assert.Empty(bed.Meta.StreamUploads);
        Assert.Empty(bed.Outbound.Claims);
    }

    [Fact]
    public async Task AnExtensionThatCouldNeverBeStagedIsUnsupportedWithoutTouchingR2OrTheOwner()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() =>
            bed.SendMediaHandler().HandleAsync(new SendMediaMessageCommand(bed.TenantId, conversation.Id, bed.ClientId, Guid.CreateVersion7(), "a.webp", null), Ct));

        Assert.Equal(MessagingErrorCodes.MediaUnsupportedFormat, error.Code);
        Assert.Empty(bed.Staging.Stats);
        Assert.Empty(bed.Repository.AutoAssigned);
    }

    // S13: firma de JPEG pero no decodifica.
    [Fact]
    public async Task AnImageThatDoesNotDecodeIsInvalidAndTheStagingIsDeleted()
    {
        var bed = new MessagingTestBed();
        var staged = Prepare(bed, "rota.jpg", Jpeg);
        bed.Compressor.Result = null;

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => bed.SendMediaHandler().HandleAsync(Command(bed, staged, "rota.jpg"), Ct));

        Assert.Equal(MessagingErrorCodes.MediaInvalidImage, error.Code);
        Assert.Equal([staged.StagingKey], bed.Store.Deleted);
        Assert.Empty(bed.Meta.StreamUploads);
        Assert.Empty(bed.Outbound.Claims);
    }

    // R6, S16, S26: cada guarda corta antes de leer R2 o llamar a Meta; con la conexión pausada tampoco se reclama.
    [Theory]
    [InlineData("window-closed", MessagingErrorCodes.WindowClosed)]
    [InlineData("assigned-to-other", MessagingErrorCodes.AssignedToOther)]
    [InlineData("connection-paused", MessagingErrorCodes.ConnectionUnavailable)]
    public async Task TheChecksCutBeforeTheStagingIsReadAndKeepItForALaterTry(string scenario, string expected)
    {
        var bed = new MessagingTestBed();
        var staged = Prepare(bed, "a.pdf", Pdf);
        bed.Conversations.Rows.Clear();
        var conversation = scenario switch
        {
            "window-closed" => bed.OpenConversation(lastInboundHoursAgo: 25),
            "assigned-to-other" => bed.OpenConversation(lastInboundHoursAgo: 1, assignedTo: Guid.CreateVersion7()),
            _ => bed.OpenConversation(lastInboundHoursAgo: 1),
        };
        bed.Store.Objects.Remove(staged.StagingKey);
        staged = new Staged(conversation, staged.UploadId, bed.Stage(conversation.Id, staged.UploadId, "a.pdf", Pdf));
        if (scenario == "connection-paused")
        {
            bed.Connections.Sender = null;
        }

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => bed.SendMediaHandler().HandleAsync(Command(bed, staged, "a.pdf"), Ct));

        Assert.Equal(expected, error.Code);
        Assert.Empty(bed.Meta.StreamUploads);
        Assert.Empty(bed.Outbound.Claims);
        Assert.Empty(bed.Store.Deleted);
        Assert.True(bed.Store.Objects.ContainsKey(staged.StagingKey));
    }

    // S17: sin dueño, enviar es tomar (una vez validado todo lo que no cambia estado).
    [Fact]
    public async Task SendingOnAnUnassignedConversationTakesIt()
    {
        var bed = new MessagingTestBed();
        var staged = Prepare(bed, "a.pdf", Pdf, assigned: false);

        await bed.SendMediaHandler().HandleAsync(Command(bed, staged, "a.pdf"), Ct);

        Assert.Equal([staged.Conversation.Id], bed.Repository.AutoAssigned);
    }

    // S14b: una cita inválida no autoasigna ni toca R2 ni Meta.
    [Fact]
    public async Task AnInvalidReplyToIsAValidationErrorBeforeTheOwnerR2AndMeta()
    {
        var bed = new MessagingTestBed();
        var staged = Prepare(bed, "a.pdf", Pdf, assigned: false);

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            bed.SendMediaHandler().HandleAsync(Command(bed, staged, "a.pdf", replyTo: Guid.CreateVersion7()), Ct));

        Assert.Contains(error.Errors, failure => failure.PropertyName == "replyTo");
        Assert.Empty(bed.Repository.AutoAssigned);
        Assert.Empty(bed.Staging.Stats);
        Assert.Empty(bed.Meta.StreamUploads);
    }

    // S18: otro tenant es 403, nunca 404; una conversación que no existe en el tenant, 404.
    [Fact]
    public async Task AnotherTenantIsForbiddenAndAMissingConversationIsNotFound()
    {
        var bed = new MessagingTestBed();
        var staged = Prepare(bed, "a.pdf", Pdf);

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            bed.SendMediaHandler(tenantId: Guid.CreateVersion7()).HandleAsync(Command(bed, staged, "a.pdf"), Ct));
        await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            bed.SendMediaHandler().HandleAsync(new SendMediaMessageCommand(bed.TenantId, Guid.CreateVersion7(), bed.ClientId, staged.UploadId, "a.pdf", null), Ct));

        Assert.Empty(bed.Staging.Stats);
        Assert.Empty(bed.Outbound.Claims);
    }

    // S24, S25, DP-3 y S4: Failed guarda la fila con el objeto final y conserva el staging para el reintento con el mismo clientId.
    [Theory]
    [InlineData(400, 100, "committed-failed:100")]
    [InlineData(500, null, "committed-failed:-1")]
    [InlineData(0, null, "committed-failed:-1")]
    public async Task AFailedUploadToMetaKeepsTheCompressedImageAndTheStagingWithoutSending(int httpStatus, int? graphCode, string expectedClaim)
    {
        var bed = new MessagingTestBed();
        var staged = Prepare(bed, "a.jpg", Jpeg);
        bed.Meta.NextUpload = new MessagingGraphResult<string>(null, new MessagingGraphFailure(httpStatus, graphCode, "x"));

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => bed.SendMediaHandler().HandleAsync(Command(bed, staged, "a.jpg"), Ct));

        Assert.Equal(MessagingErrorCodes.MessageRejected, error.Code);
        var claim = Assert.Single(bed.Outbound.Claims);
        Assert.Equal((expectedClaim, (string?)null), (claim.Outcome, claim.MetaMediaId));
        Assert.Empty(bed.Meta.ImageSends);
        Assert.Equal(FakeImageCompressor.Output, bed.Store.Objects[FinalKeyOf(bed, claim)].Bytes);
        Assert.True(bed.Store.Objects.ContainsKey(staged.StagingKey));
        Assert.Empty(bed.Store.Deleted);
    }

    [Fact]
    public async Task ADocumentThatMetaRejectsWhenSendingIsFailedWithItsCodeAndKeepsTheFinalObjectAndTheStaging()
    {
        var bed = new MessagingTestBed();
        var staged = Prepare(bed, "a.pdf", Pdf);
        bed.Meta.NextSend = new SendResult(SendOutcome.GraphError, null, 131053, "Media upload error");

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => bed.SendMediaHandler().HandleAsync(Command(bed, staged, "a.pdf"), Ct));

        Assert.Equal(MessagingErrorCodes.MessageRejected, error.Code);
        var claim = Assert.Single(bed.Outbound.Claims);
        Assert.Equal("committed-failed:131053", claim.Outcome);
        Assert.True(bed.Store.Objects.ContainsKey(FinalKeyOf(bed, claim)));
        Assert.True(bed.Store.Objects.ContainsKey(staged.StagingKey));
        Assert.Empty(bed.Store.Deleted);
    }

    // Una credencial vencida no guarda la fila: el objeto final que se escribió no tendría quién lo sirva y se borra; el staging queda.
    [Fact]
    public async Task ARejectedCredentialRollsBackDeletesTheFinalObjectKeepsTheStagingAndReportsTheConnection()
    {
        var bed = new MessagingTestBed();
        var staged = Prepare(bed, "a.jpg", Jpeg);
        bed.Meta.NextSend = new SendResult(SendOutcome.GraphError, null, MetaErrorCodes.TokenExpired, null);

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => bed.SendMediaHandler().HandleAsync(Command(bed, staged, "a.jpg"), Ct));

        Assert.Equal(MessagingErrorCodes.ConnectionUnavailable, error.Code);
        var claim = Assert.Single(bed.Outbound.Claims);
        Assert.Equal("rolled-back", claim.Outcome);
        Assert.Equal([FinalKeyOf(bed, claim)], bed.Store.Deleted);
        Assert.True(bed.Store.Objects.ContainsKey(staged.StagingKey));
        Assert.Single(bed.Connections.Reported);
    }

    // Una falla de R2 al escribir el final: nada llegó a Meta como envío y la fila no queda, reintentar es seguro.
    [Fact]
    public async Task AFailureWritingTheFinalImageIsUploadFailedWithoutRowAndTheStagingStays()
    {
        var bed = new MessagingTestBed();
        var staged = Prepare(bed, "a.jpg", Jpeg);
        bed.Store.UploadFailure = new IOException("R2 no responde");

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => bed.SendMediaHandler().HandleAsync(Command(bed, staged, "a.jpg"), Ct));

        Assert.Equal(MessagingErrorCodes.MediaUploadFailed, error.Code);
        Assert.IsType<IOException>(error.InnerException);
        Assert.Equal("rolled-back", Assert.Single(bed.Outbound.Claims).Outcome);
        Assert.Empty(bed.Meta.ImageSends);
        Assert.True(bed.Store.Objects.ContainsKey(staged.StagingKey));
    }

    [Fact]
    public async Task AFailedPromoteOfADocumentIsUploadFailedWithoutRowAndTheStagingStays()
    {
        var bed = new MessagingTestBed();
        var staged = Prepare(bed, "a.pdf", Pdf);
        bed.Staging.PromoteFailure = new IOException("ETag distinto");

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => bed.SendMediaHandler().HandleAsync(Command(bed, staged, "a.pdf"), Ct));

        Assert.Equal(MessagingErrorCodes.MediaUploadFailed, error.Code);
        Assert.Equal("rolled-back", Assert.Single(bed.Outbound.Claims).Outcome);
        Assert.Empty(bed.Meta.DocumentSends);
        Assert.True(bed.Store.Objects.ContainsKey(staged.StagingKey));
    }

    // R14: un Failed no cuenta como enviado; el mismo clientId reenvía sobre la misma fila (mismo messageId) y esta vez el staging se borra.
    [Fact]
    public async Task RetryingAFailedClientIdResendsOverTheSameRowAndThenDeletesTheStaging()
    {
        var bed = new MessagingTestBed();
        var staged = Prepare(bed, "a.pdf", Pdf);
        var failed = new ExistingOutbound(
            Guid.CreateVersion7(), MessageStatus.Failed, bed.Now, bed.MemberId, string.Empty, null, MessageKind.Document, "application/pdf", null, "a.pdf", null);
        bed.Outbound.Committed = failed;
        bed.Outbound.Existing = failed;

        var message = await bed.SendMediaHandler().HandleAsync(Command(bed, staged, "a.pdf"), Ct);

        Assert.Equal(failed.Id, message.Id);
        Assert.Equal("committed-sent:wamid.default", Assert.Single(bed.Outbound.Claims).Outcome);
        Assert.Equal([staged.StagingKey], bed.Store.Deleted);
    }

    // Borrar el staging es best effort y va con CancellationToken.None: ya se envió, no hay por qué cambiar la respuesta.
    [Fact]
    public async Task ADeleteFailureOfTheStagingAfterASendDoesNotChangeTheAnswer()
    {
        var bed = new MessagingTestBed();
        var staged = Prepare(bed, "a.pdf", Pdf);
        bed.Store.DeleteFailure = new IOException("R2 no responde");

        var message = await bed.SendMediaHandler().HandleAsync(Command(bed, staged, "a.pdf"), Ct);

        Assert.Equal("Sent", message.Status);
        Assert.Equal([CancellationToken.None], bed.Store.DeleteTokens);
    }

    // Spec 2026-10-20 D3 y D5: un video va intacto por la subida de documentos, sale como type video con su texto y queda Video.
    [Fact]
    public async Task AVideoTravelsIntactAndIsSentAsAVideoWithItsCaption()
    {
        var bed = new MessagingTestBed();
        var staged = Prepare(bed, "Producto.mp4", Mp4);
        bed.Meta.NextUpload = new MessagingGraphResult<string>("media.vid", null);
        bed.Meta.NextSend = new SendResult(SendOutcome.Sent, "wamid.vid", null, null);

        var message = await bed.SendMediaHandler().HandleAsync(Command(bed, staged, "Producto.mp4", "Mira el producto"), Ct);

        Assert.Equal(("Video", "Sent"), (message.Kind, message.Status));
        Assert.Equal(new MediaDto($"/api/v1/tenants/{bed.TenantId}/messaging/media/{message.Id}", "video/mp4", "Producto.mp4", "Mira el producto"), message.Media);
        var upload = Assert.Single(bed.Meta.StreamUploads);
        Assert.Equal(Mp4, upload.Content);
        Assert.Equal(((long)Mp4.Length, "video/mp4", "video.mp4"), (upload.DeclaredLength, upload.MimeType, upload.FileName));
        var send = Assert.Single(bed.Meta.VideoSends);
        Assert.Equal(("media.vid", "Mira el producto", $"qep:{message.Id}"), (send.MediaId, send.Caption, send.CallbackData));
        Assert.Empty(bed.Meta.DocumentSends);
        Assert.Empty(bed.Meta.ImageSends);
        var claim = Assert.Single(bed.Outbound.Claims);
        Assert.Equal((MessageKind.Video, "Mira el producto"), (claim.Draft.Kind, claim.Draft.Caption));
        Assert.Equal(new OutboundMedia("video/mp4", Mp4.Length, Convert.ToHexStringLower(SHA256.HashData(Mp4)), "Producto.mp4"), claim.Draft.Media);
        Assert.Equal((staged.StagingKey, FinalKeyOf(bed, claim), FakeUploadStaging.ETag), Assert.Single(bed.Staging.Promoted));
    }

    // D5 y D13 del frontend: un audio adjunto sale sin voice y guarda su nombre; la nota de voz guarda null, así se distinguen.
    [Theory]
    [InlineData("Llamada.m4a", "audio/mp4", "audio.m4a")]
    [InlineData("Canción.mp3", "audio/mpeg", "audio.mp3")]
    public async Task AFileAudioIsSentWithoutTheVoiceFlagAndKeepsItsFileName(string fileName, string mimeType, string multipartName)
    {
        var bed = new MessagingTestBed();
        var bytes = fileName.EndsWith(".m4a", StringComparison.Ordinal) ? M4a : Mp3;
        var staged = Prepare(bed, fileName, bytes);
        bed.Meta.NextUpload = new MessagingGraphResult<string>("media.aud", null);

        var message = await bed.SendMediaHandler().HandleAsync(Command(bed, staged, fileName), Ct);

        Assert.Equal(("Audio", "Sent"), (message.Kind, message.Status));
        Assert.Equal(new MediaDto($"/api/v1/tenants/{bed.TenantId}/messaging/media/{message.Id}", mimeType, fileName, null), message.Media);
        Assert.Equal((mimeType, multipartName), (Assert.Single(bed.Meta.StreamUploads).MimeType, bed.Meta.StreamUploads[0].FileName));
        var send = Assert.Single(bed.Meta.AudioSends);
        Assert.Equal(("media.aud", false), (send.MediaId, send.Voice));
        var claim = Assert.Single(bed.Outbound.Claims);
        Assert.Equal((MessageKind.Audio, (string?)null, fileName), (claim.Draft.Kind, claim.Draft.Caption, claim.Draft.Media!.FileName));
    }

    // D9: el backend no mira el códec; si Meta rechaza el video, el mensaje queda Failed con el código de Meta, como un documento.
    [Fact]
    public async Task AVideoThatMetaRejectsIsFailedWithItsCode()
    {
        var bed = new MessagingTestBed();
        var staged = Prepare(bed, "hevc.mp4", Mp4);
        bed.Meta.NextSend = new SendResult(SendOutcome.GraphError, null, 131053, "Media upload error");

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => bed.SendMediaHandler().HandleAsync(Command(bed, staged, "hevc.mp4"), Ct));

        Assert.Equal(MessagingErrorCodes.MessageRejected, error.Code);
        Assert.Equal("committed-failed:131053", Assert.Single(bed.Outbound.Claims).Outcome);
        Assert.True(bed.Store.Objects.ContainsKey(staged.StagingKey));
    }

    // D6: en Instagram el video y el audio van por URL firmada con su attachment.type.
    [Theory]
    [InlineData("Producto.mp4", "video", "Video")]
    [InlineData("Llamada.m4a", "audio", "Audio")]
    [InlineData("Canción.mp3", "audio", "Audio")]
    public async Task OnInstagramAVideoOrAnAudioGoesByASignedUrlWithItsAttachmentType(string fileName, string attachmentType, string kind)
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1, assignedTo: bed.MemberId, userId: "1789", waId: null, channel: ConversationChannel.Instagram);
        var uploadId = Guid.CreateVersion7();
        var bytes = fileName.EndsWith(".mp4", StringComparison.Ordinal) ? Mp4 : fileName.EndsWith(".m4a", StringComparison.Ordinal) ? M4a : Mp3;
        var staged = new Staged(conversation, uploadId, bed.Stage(conversation.Id, uploadId, fileName, bytes));
        bed.Instagram.NextSend = new SendResult(SendOutcome.Sent, "mid.1", null, null);

        var message = await bed.SendMediaHandler().HandleAsync(Command(bed, staged, fileName), Ct);

        Assert.Equal((kind, "Sent", fileName), (message.Kind, message.Status, message.Media!.FileName));
        var sent = Assert.Single(bed.Instagram.SentAttachments);
        Assert.Equal(("1789", attachmentType), (sent.RecipientId, sent.Type));
        Assert.Empty(bed.Meta.StreamUploads);
    }

    // D2: el tope del canal también se mide al confirmar, sobre el objeto real.
    [Fact]
    public async Task AVideoOverTheWhatsAppLimitIsTooLargeAtConfirmation()
    {
        var bed = new MessagingTestBed();
        var staged = Prepare(bed, "largo.mp4", Mp4);
        bed.Staging.StatSize = 16L * 1024 * 1024 + 1;

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => bed.SendMediaHandler().HandleAsync(Command(bed, staged, "largo.mp4"), Ct));

        Assert.Equal(MessagingErrorCodes.MediaTooLarge, error.Code);
        Assert.Empty(bed.Meta.StreamUploads);
    }
}
