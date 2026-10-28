using System.Security.Cryptography;
using BuildingBlocks.Application;
using FluentValidation;
using FluentValidation.Results;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

/// <summary>Spec 2026-10-11 §6.1: el endpoint copia <c>file</c> a memoria (≤ 17 MB por el tope del cuerpo) para que
/// el handler calcule el hash, revise la cabecera y suba dos veces (R2 y Meta) sin volver a leer el request.</summary>
public sealed record SendAudioMessageCommand(Guid TenantId, Guid ConversationId, Guid? ClientId, byte[]? Audio, Guid? ReplyTo = null) : ICommand<MessageDto>;

/// <summary>Spec 2026-10-11 §6.2: <c>clientId</c> GUID no vacío; <c>replyTo</c>, si viene, no vacío; <c>file</c> de
/// 1 B a <see cref="MaxAudioBytes"/> y OGG/Opus por contenido (<see cref="OggOpusSniffer"/>). Las claves de
/// <c>errors</c> son las del form (<c>OverridePropertyName</c>).</summary>
public sealed class SendAudioMessageValidator : AbstractValidator<SendAudioMessageCommand>
{
    /// <summary>§3: el tope de Meta para un audio.</summary>
    public const int MaxAudioBytes = 16 * 1024 * 1024;

    public SendAudioMessageValidator()
    {
        RuleFor(command => command.ClientId).NotNull().NotEqual(Guid.Empty).OverridePropertyName("clientId");
        RuleFor(command => command.ReplyTo).NotEqual(Guid.Empty).When(command => command.ReplyTo is not null).OverridePropertyName("replyTo");
        // Primero el tamaño: un archivo vacío o enorme no se inspecciona.
        RuleFor(command => command.Audio)
            .Cascade(CascadeMode.Stop)
            .Must(audio => audio is { Length: > 0 and <= MaxAudioBytes }).WithMessage("Graba una nota de voz de hasta 16 MB.")
            .Must(audio => OggOpusSniffer.IsOggOpus(audio!)).WithMessage("La nota de voz no está en el formato que admite WhatsApp. Graba de nuevo.")
            .OverridePropertyName("file");
    }
}

/// <summary>
/// Spec 2026-10-11 §6.3: mismo orden que <see cref="SendMessageHandler"/> (spec 2026-10-10 §8.5), con las mismas
/// guardas (<see cref="OutboundGuards"/>). Dentro del reclamo, en este orden: R2 → subida a Meta → envío con
/// <c>voice: true</c> → la tabla del texto. El candado de la fila dura las tres llamadas (≤ 30 s la subida a Meta en
/// <c>messaging.meta-graph-upload</c>, ≤ 10 s el envío en <c>messaging.meta-graph</c>, R2 con su propio tope) y sigue
/// bloqueando una sola fila de mensaje, nunca la conversación.
/// </summary>
public sealed class SendAudioMessageHandler(
    IConversationQueries conversations,
    IConversationRepository repository,
    IMessageQueries messages,
    IOutboundMessages outbound,
    IWhatsAppCloudClient meta,
    IInstagramClient instagram,
    IOpusTranscoder transcoder,
    IMessagingMediaStore store,
    IMessagingConnectionDirectory connections,
    IMessagingMemberNames memberNames,
    IMembershipDirectory membershipDirectory,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock,
    IMessagingNotifier notifier,
    ReadReceipts receipts,
    IAutomationsChatbotNames chatbotNames,
    IValidator<SendAudioMessageCommand> validator)
    : ICommandHandler<SendAudioMessageCommand, MessageDto>
{
    // CS9124: los parámetros que van a la guarda no se usan en los métodos; el reloj y el reclamo se piden a ella.
    private readonly OutboundGuards _guards = new(
        conversations, repository, messages, outbound, connections, memberNames, membershipDirectory, tenantModules, executionContext, clock, notifier, receipts, chatbotNames);

    public async Task<MessageDto> HandleAsync(SendAudioMessageCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var target = await _guards.LoadAsync(command.TenantId, command.ConversationId, cancellationToken);
        var conversation = target.Conversation;

        // §4.1: repetir un clientId que no está Failed devuelve la nota guardada, aunque el archivo del request sea otro.
        if (await _guards.FindAlreadySentAsync(conversation.Id, command.ClientId!.Value, cancellationToken) is { } sent)
        {
            return await _guards.ToDtoAsync(command.TenantId, OutboundGuards.ExistingRow(conversation.Id, sent, command.ClientId), cancellationToken);
        }

        // Spec 2026-10-14 decisión 16: Instagram no acepta Opus; la nota pasa a WAV antes de admitir, para que una grabación que no
        // decodifica sea un 422 del campo y no cambie el dueño. Se guarda y se envía el WAV: es lo que la persona recibió.
        var isInstagram = conversation.Channel == ConversationChannel.Instagram;
        var audio = command.Audio!;
        var mimeType = OggOpusSniffer.MimeType;
        if (isInstagram)
        {
            audio = Transcode(audio);
            mimeType = InstagramAudio.WavMimeType;
        }

        var permit = await _guards.AdmitAsync(command.TenantId, target, command.ReplyTo, cancellationToken);
        // El validador ya garantizó el audio: presente, de 1 B a 16 MB y OGG/Opus.
        var media = new OutboundMedia(mimeType, audio.Length, Convert.ToHexStringLower(SHA256.HashData(audio)));
        var draft = new OutboundDraft(
            Guid.CreateVersion7(), conversation.Id, command.TenantId, conversation.ConnectionId, command.ClientId!.Value, Text: null, target.Member, permit.Now,
            permit.Quoted?.Id, permit.Quoted?.Wamid, MessageKind.Audio, media);
        await using var claim = await _guards.ClaimAsync(draft, cancellationToken);

        if (await OutboundGuards.TakeInFlightAsync(claim) is { } inFlight)
        {
            return await _guards.ToDtoAsync(command.TenantId, OutboundGuards.ExistingRow(conversation.Id, inFlight, command.ClientId), cancellationToken);
        }

        MessagingSender? sender = null;
        InstagramSender? igSender = null;
        if (isInstagram)
        {
            igSender = await _guards.ResolveInstagramSenderAsync(command.TenantId, conversation.ConnectionId, claim, cancellationToken);
        }
        else
        {
            sender = await _guards.ResolveSenderAsync(command.TenantId, conversation.ConnectionId, claim, cancellationToken);
        }

        // 1. R2, con la clave del reclamo: un reintento de un Failed reutiliza el messageId y sobrescribe el mismo objeto.
        var key = MessagingMediaKeys.For(command.TenantId, claim.MessageId);
        try
        {
            using var content = new MemoryStream(audio, writable: false);
            await store.UploadAsync(key, content, audio.Length, mimeType, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // No se llamó a Meta y la fila no queda: reintentar con el mismo clientId es seguro.
            await claim.RollbackAsync(CancellationToken.None);
            throw new MessagingDomainException(MessagingErrorCodes.MediaUploadFailed, "The voice note could not be stored.", exception);
        }

        // 2 y 3. Desde acá, como el texto, todo va con CancellationToken.None: si Meta ya aceptó, cortar por la pestaña
        // cerrada borraría la fila y el reintento mandaría la nota dos veces. Una subida fallida no envió nada todavía, así
        // que entra a la misma tabla sin riesgo de duplicar.
        string? mediaId = null;
        DateTimeOffset answeredAt;
        try
        {
            SendResult result;
            if (isInstagram)
            {
                // Spec 2026-10-14 decisión 12: Meta baja el WAV desde una URL firmada de R2; no hay subida previa.
                result = await SendInstagramAudioAsync(igSender!, conversation.UserId!, key);
            }
            else
            {
                var upload = await meta.UploadMediaAsync(sender!, audio, OggOpusSniffer.MimeType, CancellationToken.None);
                mediaId = upload is { Succeeded: true, Value: { Length: > 0 } uploaded } ? uploaded : null;
                result = mediaId is null
                    ? FromUploadFailure(upload.Failure)
                    : await meta.SendAudioAsync(
                        sender!, SendTarget.For(conversation.UserId, conversation.WaId), mediaId, voice: true, SendMessageHandler.CallbackPrefix + claim.MessageId.ToString("D"),
                        permit.Quoted?.Wamid, CancellationToken.None);
            }

            answeredAt = _guards.UtcNow;

            // 4. La tabla del texto. Un rollback por credencial deja sin fila lo que subió una fila nueva: se borra de R2. En
            // un reintento de un Failed el objeto es el que esa fila ya sirve, y se queda.
            await _guards.SettleAsync(
                command.TenantId, conversation.ConnectionId, claim, result, answeredAt, mediaId, claim.Inserted ? () => DiscardStoredAsync(key) : null,
                seen: conversation.UnreadCount);
        }
        catch (Exception exception) when (claim.Inserted && exception is not MessagingDomainException)
        {
            // Una cancelación, una falla del commit o un bug: el reclamo se deshace al desecharse y la fila nueva no queda,
            // así que el objeto de R2 no tendría quién lo sirva. Las MessagingDomainException de SettleAsync llegan después
            // de un commit Failed (la fila sirve el objeto) o de su propio borrado por credencial: ésas no se tocan.
            await DiscardStoredAsync(key);
            throw;
        }

        return await _guards.ToDtoAsync(
            command.TenantId,
            new MessageRow(claim.MessageId, conversation.Id, MessageDirection.Outbound, MessageKind.Audio, null, null, null, MessageStatus.Sent, null, answeredAt, target.Member,
                command.ClientId, new MessageMediaRow(mimeType, null), permit.Quoted?.Id),
            CancellationToken.None);
    }

    /// <summary>Decisión 16: OGG/Opus → WAV. Un OGG que no decodifica es un 422 de <c>file</c>, como el que no es Opus; un WAV más
    /// grande que el tope de Instagram es <c>media_too_large</c> (una nota de 16 MB de Opus serían horas de audio).</summary>
    private byte[] Transcode(byte[] oggOpus)
    {
        byte[] wav;
        try
        {
            wav = transcoder.ToWav(oggOpus);
        }
        catch (InvalidDataException)
        {
            throw new ValidationException([new ValidationFailure("file", "La nota de voz no se pudo leer. Graba de nuevo.")]);
        }

        if (wav.Length is 0 or > (int)InstagramAudio.MaxBytes)
        {
            throw new MessagingDomainException(MessagingErrorCodes.MediaTooLarge, "The voice note is bigger than Instagram allows.");
        }

        return wav;
    }

    private async Task<SendResult> SendInstagramAudioAsync(InstagramSender sender, string recipientId, string key)
    {
        Uri url;
        try
        {
            url = await store.CreateDownloadUrlAsync(key, SendMediaMessageHandler.InstagramDownloadLifetime, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Sin URL no se envió nada: la fila queda Failed sin riesgo de duplicar.
            return new SendResult(SendOutcome.NotSent, null, null, null);
        }

        return await instagram.SendAttachmentAsync(sender, recipientId, InstagramAudio.AttachmentType, url, CancellationToken.None);
    }

    /// <summary>§6.3 paso 2: una subida fallida usa la tabla del envío. Un 4xx es un rechazo con su código (190/133010
    /// pasan la conexión a NeedsAttention); 5xx, timeout, red o un 2xx sin <c>id</c> no confirman nada.</summary>
    internal static SendResult FromUploadFailure(MessagingGraphFailure? failure) =>
        failure is { HttpStatus: >= 400 and < 500 }
            ? new SendResult(SendOutcome.GraphError, null, failure.Code, null)
            : new SendResult(SendOutcome.Unconfirmed, null, null, null);

    // Best effort: si borrar falla queda un objeto sin fila en el bucket privado, que nadie puede pedir (el GET de media
    // va por la fila). No vale cambiar la respuesta por eso.
    private async Task DiscardStoredAsync(string key)
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
