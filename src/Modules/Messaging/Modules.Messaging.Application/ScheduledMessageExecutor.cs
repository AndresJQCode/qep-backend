using BuildingBlocks.Application;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

/// <summary>
/// Spec 2026-11-09 «Ejecución» y «Fallas de almacenamiento»: envía un programado que el worker ya pasó a <c>Sending</c> con su lease. Sin
/// <see cref="IExecutionContext"/>, como <see cref="BotOutbound"/>. No mira estado, asignación ni asistente (D4): no reabre, no asigna y
/// liquida con <c>seen: 0</c>, sin acuse de lectura (preflight P17). Sí mira lo ya enviado con el <c>clientId</c> (D8b), bloqueo, ventana y
/// conexión, antes de reclamar.
/// <para>
/// El adjunto se copia a la clave final y se sube a Meta <b>antes</b> del reclamo: una falla ahí no dejó fila ni mandó nada y es
/// <c>media_missing</c>, reintentable. Después del reclamo, una respuesta de Meta la liquida <see cref="OutboundSettlement"/> y cualquier
/// otra falla es <c>unknown_outcome</c>: el mensaje pudo haber salido y nunca se reenvía solo. Todo lo que escribe después del reclamo va
/// con <c>CancellationToken.None</c>.
/// </para>
/// </summary>
public sealed class ScheduledMessageExecutor(
    IConversationQueries conversations,
    IConversationRepository repository,
    IOutboundMessages outbound,
    IWhatsAppCloudClient meta,
    IInstagramClient instagram,
    IMessagingMediaStore store,
    IMessagingUploadStaging staging,
    IMessagingImageCompressor compressor,
    IMessagingConnectionDirectory connections,
    IMessagingNotifier notifier,
    ReadReceipts receipts,
    IScheduledMessageStore scheduled,
    ITenantClock tenantClock,
    IClock clock)
{
    // Propiedad y no campo: meta y store también se usan en los métodos, y capturarlos para un campo es CS9124.
    private OutboundMediaPreparer Preparer => new(meta, store, compressor);

    public async Task ExecuteAsync(ClaimedScheduledMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        // 1. Gap G6: sin conversación no hay nada que cerrar; el FK en cascada se lleva la fila del programado con ella.
        var conversation = await conversations.FindAsync(message.TenantId, message.ConversationId, cancellationToken);
        if (conversation is null)
        {
            return;
        }

        // 2. Lo que ya salió con este clientId, ya salió. Un Failed descartado (D8b) quemó el clientId: es final y no se reenvía.
        var existing = await outbound.FindByClientIdAsync(conversation.Id, message.ClientId, cancellationToken);
        if (existing is { Status: not MessageStatus.Failed })
        {
            await CompleteSentAsync(message, existing.Id);
            return;
        }

        if (existing is { RetryDisabled: true })
        {
            await CompleteFailedAsync(message, MessagingErrorCodes.MessageNotRetriable);
            return;
        }

        // 3. Bloqueo, ventana y conexión, sin reclamo ni llamada a Meta. Estado, asignación y asistente no se miran (D4).
        if (conversation.BlockedAt is not null)
        {
            await CompleteFailedAsync(message, MessagingErrorCodes.ContactBlocked);
            return;
        }

        if (conversation.LastInboundAt is not { } lastInbound || lastInbound.Add(Conversation.WindowLength) <= clock.UtcNow)
        {
            await CompleteFailedAsync(message, MessagingErrorCodes.WindowClosed);
            return;
        }

        MessagingSender? sender = null;
        InstagramSender? igSender = null;
        if (conversation.Channel == ConversationChannel.Instagram)
        {
            igSender = await connections.ResolveInstagramSenderAsync(message.TenantId, conversation.ConnectionId, cancellationToken);
        }
        else
        {
            sender = (await connections.ResolveSenderWithReasonAsync(message.TenantId, conversation.ConnectionId, cancellationToken)).Sender;
        }

        if (sender is null && igSender is null)
        {
            await CompleteFailedAsync(message, MessagingErrorCodes.ConnectionUnavailable);
            return;
        }

        // 4. D5: {saludo} con la hora del tenant en este momento, en el texto o en el pie.
        var (text, caption) = await ResolveGreetingAsync(message, cancellationToken);

        // 5. Almacenamiento antes del reclamo. En un reintento el id es el de la fila Failed, que el reclamo reutiliza con su clave final.
        var messageId = existing?.Id ?? Guid.CreateVersion7();
        var rowExists = existing is not null;
        StoredScheduledMedia? media = null;
        if (message.Kind != MessageKind.Text)
        {
            media = await StoreMediaAsync(message, conversation, sender, messageId, rowExists, cancellationToken);
            if (media is null)
            {
                await CompleteFailedAsync(message, MessagingErrorCodes.ScheduledMediaMissing);
                return;
            }
        }

        // 6. El reclamo, con el autor del contenido (D4).
        var draft = new OutboundDraft(
            MessageId: messageId, ConversationId: conversation.Id, TenantId: message.TenantId, ConnectionId: conversation.ConnectionId,
            ClientId: message.ClientId, Text: text, SentByMemberId: message.AuthorMemberId, Now: clock.UtcNow, Kind: message.Kind,
            Media: media?.Media, Caption: caption);
        IOutboundClaim claim;
        try
        {
            claim = await outbound.ClaimAsync(draft, cancellationToken);
        }
        catch (Exception) when (media is not null && !rowExists)
        {
            // Sin reclamo no hay fila que sirva la copia.
            await DiscardAsync(MessagingMediaKeys.For(message.TenantId, messageId));
            throw;
        }

        await using (claim)
        {
            await SendClaimedAsync(message, conversation, sender, igSender, claim, messageId, text, caption, media);
        }
    }

    private async Task SendClaimedAsync(
        ClaimedScheduledMessage message, ConversationRow conversation, MessagingSender? sender, InstagramSender? igSender, IOutboundClaim claim,
        Guid draftMessageId, string? text, string? caption, StoredScheduledMedia? media)
    {
        // Preflight P6: el reclamo encontró la fila con candado. Otra pestaña la mandó por el hilo entre el paso 2 y acá, o el asesor la
        // descartó: nada se reenvía. La copia hecha con el id del borrador no la sirve nadie.
        if (!claim.Inserted && claim.Existing is { } found && (found.Status != MessageStatus.Failed || found.RetryDisabled))
        {
            await claim.RollbackAsync(CancellationToken.None);
            if (media is not null && found.Id != draftMessageId)
            {
                await DiscardAsync(MessagingMediaKeys.For(message.TenantId, draftMessageId));
            }

            if (found.Status != MessageStatus.Failed)
            {
                await CompleteSentAsync(message, found.Id);
            }
            else
            {
                await CompleteFailedAsync(message, MessagingErrorCodes.MessageNotRetriable);
            }

            return;
        }

        // 7. Desde acá, todo con CancellationToken.None: si Meta ya aceptó, cortar no puede borrar la fila.
        var finalKey = MessagingMediaKeys.For(message.TenantId, claim.MessageId);
        SendResult result;
        try
        {
            if (media is not null && claim.MessageId != draftMessageId)
            {
                // El reclamo reutilizó una fila Failed que la consulta sin candado no vio: la copia va a su clave. La subida a Meta no
                // depende del id.
                await DiscardAsync(MessagingMediaKeys.For(message.TenantId, draftMessageId));
                await staging.PromoteAsync(media.ScheduledKey, finalKey, media.ETag, CancellationToken.None);
            }

            result = await SendAsync(message, conversation, sender, igSender, claim.MessageId, finalKey, text, caption, media);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await GiveUpAsync(message, claim, media is not null ? finalKey : null);
            return;
        }

        // 8. La tabla de respuestas de Meta, sin reabrir ni asignar y sin acuse de lectura.
        try
        {
            await new OutboundSettlement(repository, connections, notifier, receipts, clock).SettleAsync(
                message.TenantId, conversation.ConnectionId, claim, result, clock.UtcNow, metaMediaId: media?.MetaMediaId,
                afterRollback: media is not null && claim.Inserted ? () => DiscardAsync(finalKey) : null, seen: 0, acknowledge: false);
        }
        catch (MessagingDomainException exception)
        {
            // message.rejected, window_closed o connection_unavailable: la tabla ya cerró el reclamo.
            await CompleteFailedAsync(message, exception.Code);
            return;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await GiveUpAsync(message, claim, media is not null ? finalKey : null);
            return;
        }

        await CompleteSentAsync(message, claim.MessageId);
    }

    private async Task<SendResult> SendAsync(
        ClaimedScheduledMessage message, ConversationRow conversation, MessagingSender? sender, InstagramSender? igSender, Guid messageId, string finalKey,
        string? text, string? caption, StoredScheduledMedia? media)
    {
        if (igSender is not null)
        {
            // Spec 2026-10-14 decisión 12: Meta baja el adjunto de una URL firmada de R2. El pie no viaja a Instagram; queda guardado.
            if (media is null)
            {
                return await instagram.SendTextAsync(igSender, conversation.UserId!, text!, CancellationToken.None);
            }

            var url = await store.CreateDownloadUrlAsync(finalKey, SendMediaMessageHandler.InstagramDownloadLifetime, CancellationToken.None);
            return await instagram.SendAttachmentAsync(igSender, conversation.UserId!, media.InstagramAttachmentType!, url, CancellationToken.None);
        }

        var recipient = SendTarget.For(conversation.UserId, conversation.WaId);
        var callback = SendMessageHandler.CallbackPrefix + messageId.ToString("D");
        var mediaId = media?.MetaMediaId;
        return message.Kind switch
        {
            MessageKind.Text => await meta.SendTextAsync(sender!, recipient, text!, callback, null, CancellationToken.None),
            MessageKind.Image => await meta.SendImageAsync(sender!, recipient, mediaId!, caption, callback, null, CancellationToken.None),
            MessageKind.Video => await meta.SendVideoAsync(sender!, recipient, mediaId!, caption, callback, null, CancellationToken.None),
            MessageKind.Audio => await meta.SendAudioAsync(sender!, recipient, mediaId!, voice: message.IsVoiceNote, callback, null, CancellationToken.None),
            MessageKind.Document => await meta.SendDocumentAsync(
                sender!, recipient, mediaId!, message.MediaFileName!, caption, callback, null, CancellationToken.None),
            _ => throw new InvalidOperationException($"A scheduled message cannot be of kind {message.Kind}."),
        };
    }

    /// <summary>Lee, copia a la clave final y (WhatsApp) sube a Meta. <c>null</c> = <c>media_missing</c>: no hubo reclamo y nada salió. La
    /// copia se borra si la clave era nueva; en un reintento es la que ya sirve la fila Failed y se queda.</summary>
    private async Task<StoredScheduledMedia?> StoreMediaAsync(
        ClaimedScheduledMessage message, ConversationRow conversation, MessagingSender? sender, Guid messageId, bool rowExists,
        CancellationToken cancellationToken)
    {
        if (message.MediaStorageKey is not { } scheduledKey || message.MediaMimeType is not { } mimeType)
        {
            return null;
        }

        var extension = Path.GetExtension(scheduledKey);
        string? instagramType = null;
        if (sender is null)
        {
            // La nota de voz de Instagram se guardó ya en WAV, que la tabla de formatos no lista.
            instagramType = message.Kind == MessageKind.Audio
                ? InstagramAudio.AttachmentType
                : OutboundMediaPolicy.FormatOf(extension) is { } format ? OutboundMediaPolicy.InstagramAttachmentType(format) : null;
            if (instagramType is null)
            {
                return null;
            }
        }

        var finalKey = MessagingMediaKeys.For(message.TenantId, messageId);
        try
        {
            var staged = await staging.StatAsync(scheduledKey, cancellationToken);
            if (staged is null)
            {
                return null;
            }

            // Task 4: el tamaño y el mime guardados, los de la imagen ya comprimida.
            var sizeBytes = message.MediaSizeBytes ?? staged.SizeBytes;
            await staging.PromoteAsync(scheduledKey, finalKey, staged.ETag, cancellationToken);
            string? mediaId = null;
            if (sender is not null)
            {
                var (upload, _) = await Preparer.UploadStoredAsync(sender, finalKey, sizeBytes, mimeType, message.Kind, extension, cancellationToken);
                if (upload is not { Succeeded: true, Value: { Length: > 0 } uploaded })
                {
                    await ReportCredentialRejectionAsync(message.TenantId, conversation.ConnectionId, upload.Failure);
                    await DiscardCopyAsync(finalKey, rowExists);
                    return null;
                }

                mediaId = uploaded;
            }

            return new StoredScheduledMedia(
                scheduledKey, staged.ETag, mediaId, instagramType, new OutboundMedia(mimeType, sizeBytes, message.MediaSha256, message.MediaFileName));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException && !(exception is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            await DiscardCopyAsync(finalKey, rowExists);
            return null;
        }
    }

    /// <summary>Decisión del orquestador (G8): la misma regla que la subida del adjunto del asesor (<see cref="SendAudioMessageHandler.FromUploadFailure"/>
    /// → <see cref="OutboundSettlement"/>): un 4xx con 190/133010 pasa la conexión a NeedsAttention. Si Integrations no guarda el cambio, el
    /// programado queda igual <c>media_missing</c>; el próximo intento vuelve a recibir el código.</summary>
    private async Task ReportCredentialRejectionAsync(Guid tenantId, Guid connectionId, MessagingGraphFailure? failure)
    {
        if (failure is not { HttpStatus: >= 400 and < 500, Code: MetaErrorCodes.TokenExpired or MetaErrorCodes.NumberUnregistered })
        {
            return;
        }

        try
        {
            await ConnectionRejection.ReportAsync(connections, tenantId, connectionId, failure.Code!.Value);
        }
        catch (MessagingDomainException)
        {
            // Ver comentario.
        }
    }

    /// <summary>Spec «Después de un reclamo exitoso»: el reclamo se deshace (una fila nueva desaparece, una Failed reutilizada queda igual),
    /// la copia se borra sólo si la fila era nueva, y el programado queda <c>unknown_outcome</c>: nunca se reenvía solo.</summary>
    private async Task GiveUpAsync(ClaimedScheduledMessage message, IOutboundClaim claim, string? finalKey)
    {
        try
        {
            await claim.RollbackAsync(CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Ya estaba cerrado, o la base no responde: desechar el reclamo sin cerrar es rollback.
        }

        if (finalKey is not null && claim.Inserted)
        {
            await DiscardAsync(finalKey);
        }

        await CompleteFailedAsync(message, MessagingErrorCodes.ScheduledUnknownOutcome);
    }

    private async Task<(string? Text, string? Caption)> ResolveGreetingAsync(ClaimedScheduledMessage message, CancellationToken cancellationToken)
    {
        if (!HasGreeting(message.Text) && !HasGreeting(message.Caption))
        {
            return (message.Text, message.Caption);
        }

        // Como CampaignDispatcher.GreetingAsync: la hora local del tenant en el instante de su propio calendario.
        var calendar = await tenantClock.GetAsync(message.TenantId, cancellationToken);
        var greeting = TimeOfDayGreeting.For(TimeOnly.FromDateTime(calendar.ToLocal(calendar.UtcNow).DateTime));
        return (Resolve(message.Text), Resolve(message.Caption));

        string? Resolve(string? value) => value is null ? null : ScheduledText.ResolveGreeting(value, greeting);
    }

    private static bool HasGreeting(string? value) => value?.Contains(ScheduledText.GreetingToken, StringComparison.Ordinal) == true;

    /// <summary>Cierre CAS. Sólo si escribió: aviso y, como <c>Sent</c> ya no necesita el adjunto programado, se borra (best effort) y se
    /// limpia su clave; si borrar falla, la clave queda para el barrido de purga.</summary>
    private async Task CompleteSentAsync(ClaimedScheduledMessage message, Guid sentMessageId)
    {
        if (!await scheduled.CompleteSentAsync(message, sentMessageId, clock.UtcNow, CancellationToken.None))
        {
            return;
        }

        await NotifyAsync(message);
        if (message.MediaStorageKey is { } key && await DiscardAsync(key))
        {
            try
            {
                await scheduled.ClearMediaKeyAsync(message.TenantId, message.Id, CancellationToken.None);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // El barrido de purga borra otra vez un objeto que ya no está y limpia la clave.
            }
        }
    }

    private async Task CompleteFailedAsync(ClaimedScheduledMessage message, string failureCode)
    {
        if (await scheduled.CompleteFailedAsync(message, failureCode, clock.UtcNow, CancellationToken.None))
        {
            await NotifyAsync(message);
        }
    }

    /// <summary>D11: el SPA invalida con el aviso. Un fallo no cambia el cierre ya escrito; el polling cubre.</summary>
    private async Task NotifyAsync(ClaimedScheduledMessage message)
    {
        try
        {
            await notifier.ConversationChangedAsync(message.TenantId, message.ConversationId, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Ver comentario.
        }
    }

    private async Task DiscardCopyAsync(string finalKey, bool rowExists)
    {
        if (!rowExists)
        {
            await DiscardAsync(finalKey);
        }
    }

    // Best effort: un objeto que ninguna fila sirve no cambia el resultado. Application no tiene logger.
    private async Task<bool> DiscardAsync(string key)
    {
        try
        {
            await store.DeleteAsync(key, CancellationToken.None);
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary>El adjunto ya copiado a la clave final (y subido a Meta en WhatsApp), con lo que hace falta para rehacer la copia si el
    /// reclamo devuelve otro id.</summary>
    private sealed record StoredScheduledMedia(string ScheduledKey, string ETag, string? MetaMediaId, string? InstagramAttachmentType, OutboundMedia Media);
}
