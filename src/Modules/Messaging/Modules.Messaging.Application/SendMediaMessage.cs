using System.Security.Cryptography;
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

/// <summary>Messaging-media-reply R3: confirma el adjunto que el front dejó en el staging. <c>FileName</c> es el que la persona vio
/// al elegir el archivo; el staging se reconstruye con su extensión, el <c>uploadId</c> y el contexto autenticado.</summary>
public sealed record SendMediaMessageCommand(
    Guid TenantId, Guid ConversationId, Guid? ClientId, Guid? UploadId, string? FileName, string? Caption, Guid? ReplyTo = null) : ICommand<MessageDto>;

/// <summary>R11 y R17: forma del cuerpo. Las claves de <c>errors</c> son las del JSON (<c>OverridePropertyName</c>). Si el tipo es
/// permitido y cuánto pesa lo decide el handler, con códigos de dominio.</summary>
public sealed class SendMediaMessageValidator : AbstractValidator<SendMediaMessageCommand>
{
    /// <summary>El tope de Meta para el pie de una imagen o un documento, y <c>messages.caption</c> es varchar(1024).</summary>
    public const int MaxCaptionLength = 1024;

    public SendMediaMessageValidator()
    {
        RuleFor(command => command.ClientId).NotNull().NotEqual(Guid.Empty).OverridePropertyName("clientId");
        RuleFor(command => command.UploadId).NotNull().NotEqual(Guid.Empty).OverridePropertyName("uploadId");
        RuleFor(command => command.ReplyTo).NotEqual(Guid.Empty).When(command => command.ReplyTo is not null).OverridePropertyName("replyTo");
        RuleFor(command => command.FileName)
            .NotEmpty().WithMessage("Escribe el nombre del archivo.")
            .MaximumLength(CreateMediaUploadSessionValidator.MaxFileNameLength).WithMessage("El nombre del archivo es demasiado largo.")
            .OverridePropertyName("fileName");
        RuleFor(command => command.Caption)
            .MaximumLength(MaxCaptionLength).WithMessage("El texto que acompaña al archivo puede tener hasta 1024 caracteres.")
            .OverridePropertyName("caption");
    }
}

/// <summary>
/// Messaging-media-reply R3 a R7, R10 y R13 a R15. Calco de <see cref="SendAudioMessageHandler"/> con un cambio de fondo: todo lo
/// lento va <b>antes</b> del reclamo (leer el staging de R2, comprobar el contenido, comprimir, subir a Meta). Una subida a Meta
/// no le manda nada a la persona y un <c>media_id</c> huérfano vence solo, así que se puede hacer sin fila; dentro del reclamo
/// queda lo mismo que en el audio (escritura en R2 y envío), y el candado de la fila no crece con el tamaño del archivo.
/// El <c>uploadId</c> es de un solo uso: se consume al quedar <c>Sent</c>. Un <c>Failed</c> conserva el staging, porque el
/// reintento con el mismo <c>clientId</c> lo necesita; el lifecycle del bucket lo limpia al día.
/// </summary>
public sealed class SendMediaMessageHandler(
    IConversationQueries conversations,
    IConversationRepository repository,
    IMessageQueries messages,
    IOutboundMessages outbound,
    IWhatsAppCloudClient meta,
    IInstagramClient instagram,
    IMessagingMediaStore store,
    IMessagingUploadStaging staging,
    IMessagingImageCompressor compressor,
    IMessagingConnectionDirectory connections,
    IMessagingMemberNames memberNames,
    IMembershipDirectory membershipDirectory,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock,
    IMessagingNotifier notifier,
    IValidator<SendMediaMessageCommand> validator)
    : ICommandHandler<SendMediaMessageCommand, MessageDto>
{
    // Toda imagen sale como JPEG (R12); el multipart lleva un nombre genérico ASCII y el real viaja en document.filename.
    private const string ImageMimeType = "image/jpeg";
    private const string GenericImageName = "image.jpg";
    private const string GenericDocumentName = "document";

    /// <summary>Spec 2026-10-14 decisión 12 y §11: cuánto vive la URL firmada con la que Meta descarga el adjunto de Instagram. Más
    /// que los 10 s del envío, por si Meta la baja después de responder.</summary>
    public static readonly TimeSpan InstagramDownloadLifetime = TimeSpan.FromMinutes(10);

    // CS9124: los parámetros que van a la guarda no se usan en los métodos; el reloj y el reclamo se piden a ella.
    private readonly OutboundGuards _guards = new(
        conversations, repository, messages, outbound, connections, memberNames, membershipDirectory, tenantModules, executionContext, clock, notifier);

    /// <summary>Lo que quedó de la preparación: el medio ya subido (o la falla de la subida) y los bytes finales si es una imagen.</summary>
    /// <paramref name="Upload"/> es <c>null</c> en Instagram: Meta descarga el adjunto desde R2 (decisión 12), no hay subida previa.</summary>
    private sealed record Prepared(
        string MimeType, string FileName, long SizeBytes, string? Sha256Hex, byte[]? Compressed, MessagingGraphResult<string>? Upload);

    public async Task<MessageDto> HandleAsync(SendMediaMessageCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var target = await _guards.LoadAsync(command.TenantId, command.ConversationId, cancellationToken);
        var conversation = target.Conversation;
        var clientId = command.ClientId!.Value;

        // §4.1: repetir un clientId que no está Failed devuelve lo guardado, aunque el cuerpo traiga otro archivo o pie.
        if (await _guards.FindAlreadySentAsync(conversation.Id, clientId, cancellationToken) is { } sent)
        {
            return await _guards.ToDtoAsync(command.TenantId, OutboundGuards.ExistingRow(conversation.Id, sent, clientId), cancellationToken);
        }

        // Una extensión que nunca pudo subirse no se mide contra R2 ni cambia el dueño.
        var fileName = Path.GetFileName(command.FileName!.Trim());
        var extension = OutboundMediaPolicy.ExtensionOf(fileName);
        if (OutboundMediaPolicy.FormatOf(extension) is not { } declared)
        {
            throw new MessagingDomainException(MessagingErrorCodes.MediaUnsupportedFormat, "Only JPEG and PNG images and PDF, Word and Excel documents can be sent.");
        }

        // Spec 2026-10-14 decisión 12: Instagram no acepta Word ni Excel.
        var channel = conversation.Channel;
        if (!OutboundMediaPolicy.IsAllowed(declared, channel))
        {
            throw new MessagingDomainException(MessagingErrorCodes.MediaUnsupportedFormat, "Instagram only accepts JPEG and PNG images and PDF documents.");
        }

        var permit = await _guards.AdmitAsync(command.TenantId, target, command.ReplyTo, cancellationToken);

        // La clave sale del contexto autenticado: el uploadId de otro tenant, conversación o miembro no existe.
        var stagingKey = MessagingMediaKeys.Staging(command.TenantId, conversation.Id, target.Member, command.UploadId!.Value, extension);
        var staged = await staging.StatAsync(stagingKey, cancellationToken);
        if (staged is null)
        {
            // Dos requests con el mismo clientId: el primero ya envió y consumió el staging. Gana la idempotencia, no el 422.
            return await _guards.FindAlreadySentAsync(conversation.Id, clientId, cancellationToken) is { } done
                ? await _guards.ToDtoAsync(command.TenantId, OutboundGuards.ExistingRow(conversation.Id, done, clientId), cancellationToken)
                : throw UploadNotFound();
        }

        // R10: el PUT prefirmado no impone tope; el stat sobre el objeto real manda.
        if (staged.SizeBytes <= 0 || staged.SizeBytes > OutboundMediaPolicy.MaxBytesOf(declared, channel))
        {
            await DiscardAsync(stagingKey);
            throw new MessagingDomainException(MessagingErrorCodes.MediaTooLarge, "The file is bigger than the channel allows for its type.");
        }

        var isInstagram = channel == ConversationChannel.Instagram;
        MessagingSender? sender = null;
        InstagramSender? igSender = null;
        if (isInstagram)
        {
            igSender = await _guards.ResolveInstagramSenderAsync(command.TenantId, conversation.ConnectionId, cancellationToken);
        }
        else
        {
            sender = await _guards.ResolveSenderAsync(command.TenantId, conversation.ConnectionId, cancellationToken);
        }

        var prepared = await PrepareAsync(sender, stagingKey, staged, fileName, extension, cancellationToken);

        var caption = string.IsNullOrWhiteSpace(command.Caption) ? null : command.Caption.Trim();
        var kind = OutboundMediaPolicy.IsImage(declared) ? MessageKind.Image : MessageKind.Document;
        var draft = new OutboundDraft(
            Guid.CreateVersion7(), conversation.Id, command.TenantId, conversation.ConnectionId, clientId, Text: null, target.Member, permit.Now,
            permit.Quoted?.Id, permit.Quoted?.Wamid, kind, new OutboundMedia(prepared.MimeType, prepared.SizeBytes, prepared.Sha256Hex, prepared.FileName), Caption: caption);
        await using var claim = await _guards.ClaimAsync(draft, cancellationToken);

        if (await OutboundGuards.TakeInFlightAsync(claim) is { } inFlight)
        {
            return await _guards.ToDtoAsync(command.TenantId, OutboundGuards.ExistingRow(conversation.Id, inFlight, clientId), cancellationToken);
        }

        // 1. R2, con la clave del reclamo: un reintento de un Failed reutiliza el messageId y sobrescribe el mismo objeto. La imagen
        // comprimida sube por stream; el documento se copia del lado del servidor, sin pasar sus bytes por la API.
        var finalKey = MessagingMediaKeys.For(command.TenantId, claim.MessageId);
        try
        {
            if (prepared.Compressed is { } bytes)
            {
                using var content = new MemoryStream(bytes, writable: false);
                await store.UploadAsync(finalKey, content, bytes.Length, ImageMimeType, cancellationToken);
            }
            else
            {
                await staging.PromoteAsync(stagingKey, finalKey, staged.ETag, cancellationToken);
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // No se envió nada y la fila no queda: reintentar con el mismo clientId es seguro (el staging sigue ahí).
            await claim.RollbackAsync(CancellationToken.None);
            throw new MessagingDomainException(MessagingErrorCodes.MediaUploadFailed, "The attachment could not be stored.", exception);
        }

        // 2. Como el audio, desde acá todo va con CancellationToken.None: si Meta ya aceptó, cortar por la pestaña cerrada borraría
        // la fila y el reintento enviaría el adjunto dos veces. Una subida fallida entra a la misma tabla del envío.
        DateTimeOffset answeredAt;
        try
        {
            string? mediaId = null;
            SendResult result;
            if (isInstagram)
            {
                // Spec 2026-10-14 decisión 12: Meta baja el adjunto desde una URL firmada de R2, de vida corta. Ni la leyenda ni la cita
                // viajan: la API de Instagram no los tiene en un adjunto; el pie queda guardado y se muestra en QEP.
                result = await SendInstagramAttachmentAsync(igSender!, conversation.UserId!, finalKey, OutboundMediaPolicy.InstagramAttachmentType(declared));
            }
            else
            {
                mediaId = prepared.Upload is { Succeeded: true, Value: { Length: > 0 } uploaded } ? uploaded : null;
                var callback = SendMessageHandler.CallbackPrefix + claim.MessageId.ToString("D");
                var recipient = SendTarget.For(conversation.UserId, conversation.WaId);
                result = mediaId is null
                    ? SendAudioMessageHandler.FromUploadFailure(prepared.Upload?.Failure)
                    : kind == MessageKind.Image
                        ? await meta.SendImageAsync(sender!, recipient, mediaId, caption, callback, permit.Quoted?.Wamid, CancellationToken.None)
                        : await meta.SendDocumentAsync(sender!, recipient, mediaId, prepared.FileName, caption, callback, permit.Quoted?.Wamid, CancellationToken.None);
            }

            answeredAt = _guards.UtcNow;

            // Un rollback por credencial deja sin fila lo que escribió una fila nueva: se borra de R2. En un reintento de un Failed
            // el objeto es el que esa fila ya sirve, y se queda.
            await _guards.SettleAsync(
                command.TenantId, conversation.ConnectionId, claim, result, answeredAt, mediaId, claim.Inserted ? () => DiscardAsync(finalKey) : null);
        }
        catch (Exception exception) when (claim.Inserted && exception is not MessagingDomainException)
        {
            await DiscardAsync(finalKey);
            throw;
        }

        // R13: Sent consume el uploadId. Failed no llega acá y conserva el staging.
        await DiscardAsync(stagingKey);
        return await _guards.ToDtoAsync(
            command.TenantId,
            new MessageRow(claim.MessageId, conversation.Id, MessageDirection.Outbound, kind, null, caption, null, MessageStatus.Sent, null, answeredAt, target.Member,
                clientId, new MessageMediaRow(prepared.MimeType, prepared.FileName), permit.Quoted?.Id),
            CancellationToken.None);
    }

    /// <summary>Spec 2026-10-14 decisión 12: la URL firmada y el envío. Si R2 no firma, no se envió nada: cae como no confirmado y la
    /// fila queda Failed, con el objeto ya escrito (el reintento con el mismo clientId lo sobrescribe).</summary>
    private async Task<SendResult> SendInstagramAttachmentAsync(InstagramSender sender, string recipientId, string key, string attachmentType)
    {
        Uri url;
        try
        {
            url = await store.CreateDownloadUrlAsync(key, InstagramDownloadLifetime, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new SendResult(SendOutcome.NotSent, null, null, null);
        }

        return await instagram.SendAttachmentAsync(sender, recipientId, attachmentType, url, CancellationToken.None);
    }

    /// <summary>Un solo GET del staging: la cabecera decide el formato (R9) y el resto sigue por el mismo stream. La imagen se
    /// comprime y sube desde memoria (≤ 1600 px, unos cientos de KB); el documento va de R2 a Meta sin pasar por memoria, con el
    /// SHA-256 calculado al pasar (<c>null</c> si Meta cortó antes de leerlo entero). Con <paramref name="sender"/> en <c>null</c>
    /// (Instagram) no hay subida a Meta: el documento sólo se verifica y se mide.</summary>
    private async Task<Prepared> PrepareAsync(
        MessagingSender? sender, string stagingKey, StagedUpload staged, string fileName, string extension, CancellationToken cancellationToken)
    {
        var opened = await store.OpenReadAsync(stagingKey, cancellationToken) ?? throw UploadNotFound();
        await using var source = opened.Content;
        var header = new byte[OutboundMediaSniffer.HeaderLength];
        var read = await source.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken);
        if (OutboundMediaSniffer.Detect(header.AsSpan(0, read), extension) is not { } format)
        {
            await DiscardAsync(stagingKey);
            throw new MessagingDomainException(MessagingErrorCodes.MediaUnsupportedFormat, "The file content does not match its type.");
        }

        await using var whole = new PrefixedHashingStream(header.AsMemory(0, read), source);
        if (!OutboundMediaPolicy.IsImage(format))
        {
            var mimeType = OutboundMediaPolicy.MimeTypeOf(format);
            if (sender is null)
            {
                // Instagram: el documento se promueve en R2 tal cual; el hash no hace falta para enviar y no se lee entero acá.
                return new Prepared(mimeType, fileName, staged.SizeBytes, null, null, null);
            }

            var upload = await meta.UploadMediaAsync(sender, whole, staged.SizeBytes, mimeType, GenericDocumentName + extension, cancellationToken);
            return new Prepared(mimeType, fileName, staged.SizeBytes, whole.BytesRead == staged.SizeBytes ? whole.FinishHex() : null, null, upload);
        }

        var compressed = await compressor.CompressAsync(whole, cancellationToken);
        if (compressed is null)
        {
            await DiscardAsync(stagingKey);
            throw new MessagingDomainException(MessagingErrorCodes.MediaInvalidImage, "The image could not be read.");
        }

        MessagingGraphResult<string>? uploaded = null;
        if (sender is not null)
        {
            using var content = new MemoryStream(compressed.Content, writable: false);
            uploaded = await meta.UploadMediaAsync(sender, content, compressed.Content.Length, ImageMimeType, GenericImageName, cancellationToken);
        }

        // Se guarda con el nombre base y .jpg: lo que viaja y lo que se guarda es la comprimida (R12).
        return new Prepared(
            ImageMimeType, Path.GetFileNameWithoutExtension(fileName) + ".jpg", compressed.Content.Length,
            Convert.ToHexStringLower(SHA256.HashData(compressed.Content)), compressed.Content, uploaded);
    }

    private static MessagingDomainException UploadNotFound() =>
        new(MessagingErrorCodes.MediaUploadNotFound, "The upload does not exist, expired or was already used.");

    // Best effort: si borrar falla queda un objeto en el bucket privado que nadie puede pedir (el GET de media va por la fila) y el
    // lifecycle de messaging/staging/ lo limpia al día. No vale cambiar la respuesta por eso.
    private async Task DiscardAsync(string key)
    {
        try
        {
            await store.DeleteAsync(key, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Ver comentario.
        }
    }
}

/// <summary>Lee primero la cabecera que ya se consumió para el sniff y después el resto, sumando el SHA-256 de todo lo que deja
/// pasar: un documento de 100 MB se lee de R2 una sola vez. No cierra el stream de origen, que es de quien lo abrió.</summary>
internal sealed class PrefixedHashingStream(ReadOnlyMemory<byte> prefix, Stream rest) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    private int _prefixRead;

    public long BytesRead { get; private set; }

    public string FinishHex() => Convert.ToHexStringLower(_hash.GetHashAndReset());

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => BytesRead;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var span = buffer.AsSpan(offset, count);
        var read = _prefixRead < prefix.Length ? CopyPrefix(span) : rest.Read(span);
        Track(span[..read]);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = _prefixRead < prefix.Length ? CopyPrefix(buffer.Span) : await rest.ReadAsync(buffer, cancellationToken);
        Track(buffer.Span[..read]);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private int CopyPrefix(Span<byte> destination)
    {
        var count = Math.Min(destination.Length, prefix.Length - _prefixRead);
        prefix.Span.Slice(_prefixRead, count).CopyTo(destination);
        _prefixRead += count;
        return count;
    }

    private void Track(ReadOnlySpan<byte> bytes)
    {
        _hash.AppendData(bytes);
        BytesRead += bytes.Length;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hash.Dispose();
        }

        base.Dispose(disposing);
    }
}
