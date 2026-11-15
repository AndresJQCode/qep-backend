using BuildingBlocks.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>Soporte fase 2 (plan E13): una respuesta de un ticket como texto en el hilo de su conversación, dentro de la ventana de 24 h.
/// <c>ClientId</c> es el id de la fila de respuesta de Soporte: un reintento con el mismo no vuelve a mandar nada. <c>UploadId</c> es el
/// adjunto (Tarea 11); en esta fase todavía no se envía.</summary>
public sealed record SupportThreadReplySend(Guid TenantId, Guid ConversationId, Guid ClientId, Guid AuthorMemberId, string Text, Guid? UploadId);

/// <summary>Soporte fase 2 (plan E13 y E14): una respuesta de un ticket por la plantilla «Aviso de respuesta». A una conversación existente
/// (<c>ConversationId</c>) o, sin conversación, a <c>WaId</c> (sólo dígitos, con indicativo) por la conexión <c>ConnectionId</c> que eligió el
/// asesor, abriendo la conversación de ese número. <c>RecipientName</c> llena <c>nombre_contacto</c>, <c>TicketNumber</c> el
/// <c>radicado</c> y <c>Text</c> la <c>respuesta</c>.</summary>
public sealed record SupportTemplateReplySend(
    Guid TenantId, Guid? ConversationId, Guid? ConnectionId, string? WaId, Guid? CustomerId,
    string RecipientName, Guid ClientId, Guid AuthorMemberId, string TicketNumber, string Text);

public abstract record SupportReplySendResult
{
    public sealed record Sent(Guid MessageId, Guid ConversationId) : SupportReplySendResult;

    /// <summary>Antes de enviar, sin fila en el hilo ni llamada a Meta (salvo <c>connection_unavailable</c> por credencial rechazada al
    /// enviar, que tampoco deja fila). <paramref name="Reason"/> es uno de <see cref="SupportReplyReasons"/>: <c>window_closed</c>,
    /// <c>contact_blocked</c>, <c>template_not_approved</c>, <c>template_text_invalid</c>, <c>recipient_invalid</c>, <c>text_invalid</c>, <c>connection_unavailable</c>,
    /// <c>conversation_not_found</c>, <c>attachment_unavailable</c>, <c>channel_unsupported</c>.</summary>
    public sealed record Rejected(string Reason) : SupportReplySendResult;

    /// <summary>Meta lo rechazó o no lo confirmó: la fila queda <c>Failed</c> en el hilo. <paramref name="Code"/> es <c>window_closed</c>
    /// (Meta cerró la ventana entre la lectura y el envío) o <c>send_failed</c>.</summary>
    public sealed record Failed(Guid? MessageId, string Code) : SupportReplySendResult;
}

/// <summary>Los motivos de <see cref="SupportReplySendResult"/>. Son contrato con el adaptador de Soporte, que los copia a sus códigos.</summary>
public static class SupportReplyReasons
{
    public const string WindowClosed = "window_closed";
    public const string ContactBlocked = "contact_blocked";
    public const string TemplateNotApproved = "template_not_approved";
    /// <summary>La respuesta del asesor no cumple las reglas de un parámetro de Meta, o el cuerpo armado pasa de 1024.</summary>
    public const string TemplateTextInvalid = "template_text_invalid";

    /// <summary>El nombre del destinatario o el radicado no cumplen las reglas de un parámetro de Meta (vacío, saltos de línea, más de
    /// cuatro espacios seguidos o más de 1024).</summary>
    public const string RecipientInvalid = "recipient_invalid";

    /// <summary>El texto no cumple la regla del asesor (<c>SendMessageValidator</c>) o pasa el tope de Instagram (1000).</summary>
    public const string TextInvalid = "text_invalid";

    public const string ConnectionUnavailable = "connection_unavailable";

    /// <summary>La conversación no existe en el tenant, o el número no sirve para abrir una.</summary>
    public const string ConversationNotFound = "conversation_not_found";

    /// <summary>El adjunto todavía no se puede enviar desde Soporte (llega con la Tarea 11).</summary>
    public const string AttachmentUnavailable = "attachment_unavailable";

    /// <summary>La conversación es de Instagram, que no tiene plantillas.</summary>
    public const string ChannelUnsupported = "channel_unsupported";

    public const string SendFailed = "send_failed";
}

/// <summary>Lo que Soporte necesita saber de una conversación para decidir cómo responder (BFF de <c>reply-options</c>): el canal por
/// nombre (<c>WhatsApp</c>/<c>Instagram</c>), si el contacto está bloqueado y cuándo cierra la ventana (<c>null</c> si el contacto nunca
/// escribió).</summary>
public sealed record SupportThreadState(Guid ConversationId, string Channel, bool Blocked, DateTimeOffset? WindowClosesAt, Guid ConnectionId);

public interface ISupportReplyOutbound
{
    /// <summary><c>null</c> si la conversación no existe o es de otro tenant.</summary>
    Task<SupportThreadState?> FindThreadAsync(Guid tenantId, Guid conversationId, CancellationToken ct);

    /// <summary>Dentro de la ventana, el texto; fuera, <c>Rejected(window_closed)</c> sin fila.</summary>
    Task<SupportReplySendResult> SendThreadAsync(SupportThreadReplySend send, CancellationToken ct);

    Task<SupportReplySendResult> SendTemplateAsync(SupportTemplateReplySend send, CancellationToken ct);
}

/// <summary>
/// Soporte fase 2 (plan E12): el envío de una respuesta de Soporte. El texto sigue a <see cref="BotOutbound"/> y la plantilla a
/// <see cref="BillingTemplateOutbound"/>: idempotente por <c>ClientId</c> (lo ya enviado se devuelve antes de mirar bloqueo o ventana, y dos
/// en vuelo se resuelven con el candado del reclamo), autor <c>AuthorMemberId</c> y sin <see cref="OutboundGuards"/>: lo autoriza Soporte con
/// su permiso, así que no exige la conversación <c>Open</c> ni que sea del asesor, no la toma, no cambia su estado ni su asignación y no resta
/// no leídos (<c>seen: 0</c>). El reclamo y la liquidación sí son los de siempre (<see cref="IOutboundMessages"/>,
/// <see cref="OutboundSettlement"/>), y la liquidación es la que apaga <c>history_managed</c>.
/// </summary>
public sealed class SupportReplyOutbound(
    IConversationQueries conversations,
    IConversationRepository repository,
    IOutboundMessages outbound,
    IWhatsAppCloudClient meta,
    IInstagramClient instagram,
    IMessagingConnectionDirectory connections,
    IMessagingNotifier notifier,
    ReadReceipts receipts,
    IClock clock,
    IConversationOpener opener,
    ISupportReplyTemplateReadiness templateReadiness,
    TemplateSenderValues senderValues) : ISupportReplyOutbound
{
    public async Task<SupportThreadState?> FindThreadAsync(Guid tenantId, Guid conversationId, CancellationToken ct)
    {
        var conversation = await conversations.FindAsync(tenantId, conversationId, ct);
        return conversation is null
            ? null
            : new SupportThreadState(
                conversation.Id, conversation.Channel.ToString(), conversation.BlockedAt is not null,
                conversation.LastInboundAt?.Add(Conversation.WindowLength), conversation.ConnectionId);
    }

    public async Task<SupportReplySendResult> SendThreadAsync(SupportThreadReplySend send, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(send);
        var conversation = await conversations.FindAsync(send.TenantId, send.ConversationId, ct);
        if (conversation is null)
        {
            return Rejected(SupportReplyReasons.ConversationNotFound);
        }

        // Lo que ya salió, ya salió, aunque después se haya cerrado la ventana o bloqueado al contacto (como el bot y el asesor).
        if (await AlreadySentAsync(conversation.Id, send.ClientId, ct) is { } sent)
        {
            return sent;
        }

        if (conversation.BlockedAt is not null)
        {
            return Rejected(SupportReplyReasons.ContactBlocked);
        }

        if (send.UploadId is not null)
        {
            return Rejected(SupportReplyReasons.AttachmentUnavailable);
        }

        var isInstagram = conversation.Channel == ConversationChannel.Instagram;
        if (!SendMessageValidator.IsAcceptableText(send.Text)
            || (isInstagram && send.Text.Trim().Length > SendMessageValidator.InstagramTextMaxLength))
        {
            return Rejected(SupportReplyReasons.TextInvalid);
        }

        // La ventana se mira antes de reclamar: un rechazo no deja fila.
        var now = clock.UtcNow;
        if (conversation.LastInboundAt is not { } lastInbound || lastInbound.Add(Conversation.WindowLength) <= now)
        {
            return Rejected(SupportReplyReasons.WindowClosed);
        }

        var text = send.Text.Trim();
        var draft = new OutboundDraft(
            Guid.CreateVersion7(), conversation.Id, send.TenantId, conversation.ConnectionId, send.ClientId, text, send.AuthorMemberId, now);
        await using var claim = await outbound.ClaimAsync(draft, ct);
        if (await OutboundGuards.TakeInFlightAsync(claim) is { } inFlight)
        {
            return new SupportReplySendResult.Sent(inFlight.Id, conversation.Id);
        }

        // Desde acá todo cierre va con CancellationToken.None: si Meta ya aceptó, cancelar no puede borrar la fila.
        SendResult result;
        var callback = SendMessageHandler.CallbackPrefix + claim.MessageId.ToString("D");
        if (isInstagram)
        {
            if (await connections.ResolveInstagramSenderAsync(send.TenantId, conversation.ConnectionId, ct) is not { } igSender)
            {
                await claim.RollbackAsync(CancellationToken.None);
                return Rejected(SupportReplyReasons.ConnectionUnavailable);
            }

            result = await instagram.SendTextAsync(igSender, conversation.UserId!, text, CancellationToken.None);
        }
        else
        {
            if ((await connections.ResolveSenderWithReasonAsync(send.TenantId, conversation.ConnectionId, ct)).Sender is not { } sender)
            {
                await claim.RollbackAsync(CancellationToken.None);
                return Rejected(SupportReplyReasons.ConnectionUnavailable);
            }

            result = await meta.SendTextAsync(sender, SendTarget.For(conversation.UserId, conversation.WaId), text, callback, null, CancellationToken.None);
        }

        return await SettleAsync(send.TenantId, conversation, claim, result);
    }

    public async Task<SupportReplySendResult> SendTemplateAsync(SupportTemplateReplySend send, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(send);

        // 1. Todo valor de la plantilla cumple las reglas de Meta, antes de tocar nada: sin saltos de línea, ≤ 1024 y sin más de cuatro
        // espacios seguidos. El nombre y el radicado vienen de la foto del ticket: si no sirven no es culpa del texto del asesor.
        if (!TemplateParameterRules.IsValid(send.RecipientName) || !TemplateParameterRules.IsValid(send.TicketNumber))
        {
            return Rejected(SupportReplyReasons.RecipientInvalid);
        }

        if (!TemplateParameterRules.IsValid(send.Text))
        {
            return Rejected(SupportReplyReasons.TemplateTextInvalid);
        }

        // 2. La conversación existente, o la conexión y el número para abrir una.
        ConversationRow? conversation = null;
        Guid connectionId;
        if (send.ConversationId is { } conversationId)
        {
            conversation = await conversations.FindAsync(send.TenantId, conversationId, ct);
            if (conversation is null)
            {
                return Rejected(SupportReplyReasons.ConversationNotFound);
            }

            if (conversation.Channel != ConversationChannel.WhatsApp)
            {
                return Rejected(SupportReplyReasons.ChannelUnsupported);
            }

            if (await AlreadySentAsync(conversation.Id, send.ClientId, ct) is { } sent)
            {
                return sent;
            }

            if (conversation.BlockedAt is not null)
            {
                return Rejected(SupportReplyReasons.ContactBlocked);
            }

            connectionId = conversation.ConnectionId;
        }
        else
        {
            if (send.ConnectionId is not { } chosen || send.WaId is null)
            {
                throw new ArgumentException("A template reply without a conversation needs the connection and the wa_id.", nameof(send));
            }

            // El teléfono es la clave de la conversación: con otra forma, el opener no podría usarlo.
            if (send.WaId is not { Length: > 0 and <= Conversation.WaIdMaxLength } waId || !waId.All(char.IsAsciiDigit))
            {
                return Rejected(SupportReplyReasons.ConversationNotFound);
            }

            // Un reintento de algo que ya salió se devuelve antes de mirar la plantilla o la conexión: pudieron cambiar después del envío.
            // Sólo busca: si no hay conversación, todavía no se crea.
            if (await opener.FindByPhoneAsync(send.TenantId, chosen, waId, ct) is { } existingId
                && await AlreadySentAsync(existingId, send.ClientId, ct) is { } sent)
            {
                return sent;
            }

            connectionId = chosen;
        }

        // 3. La fila SupportReply aprobada de esa conexión (nunca la constante: puede ser una _v2 con el texto del tenant).
        if (await templateReadiness.FindApprovedAsync(send.TenantId, connectionId, ct) is not { } template)
        {
            return Rejected(SupportReplyReasons.TemplateNotApproved);
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SystemTemplateVariables.ContactName] = send.RecipientName,
            [SupportReplyTemplateDefinition.CaseNumberKey] = send.TicketNumber,
            [SupportReplyTemplateDefinition.ReplyKey] = send.Text.Trim(),
        };
        await senderValues.AddAsync(send.TenantId, send.AuthorMemberId, SystemTemplateSend.VariablesOf(template), values, ct);
        if (SystemTemplateSend.Build(template, values) is not { } built)
        {
            return Rejected(SupportReplyReasons.TemplateTextInvalid);
        }

        // Meta limita el cuerpo ya armado a 1024: una respuesta que cabe sola como parámetro puede pasarse junto con el texto fijo.
        if (built.Text.Length > TemplateParameterRules.MaxValueLength)
        {
            return Rejected(SupportReplyReasons.TemplateTextInvalid);
        }

        // 4.
        if ((await connections.ResolveSenderWithReasonAsync(send.TenantId, connectionId, ct)).Sender is not { } sender)
        {
            return Rejected(SupportReplyReasons.ConnectionUnavailable);
        }

        // 5. Sin conversación: la del número en esa conexión, sin asignar a nadie (como el cobro). La que ya existe puede estar bloqueada.
        var now = clock.UtcNow;
        if (conversation is null)
        {
            var opened = await opener.OpenByPhoneAsync(send.TenantId, connectionId, send.WaId!, send.CustomerId, send.RecipientName, now, ct);
            conversation = await conversations.FindAsync(send.TenantId, opened.ConversationId, ct)
                ?? throw new InvalidOperationException("The conversation that was just opened could not be read.");
            if (await AlreadySentAsync(conversation.Id, send.ClientId, ct) is { } sent)
            {
                return sent;
            }

            if (conversation.BlockedAt is not null)
            {
                return Rejected(SupportReplyReasons.ContactBlocked);
            }
        }

        // 6. El reclamo y el envío.
        var draft = new OutboundDraft(
            Guid.CreateVersion7(), conversation.Id, send.TenantId, connectionId, send.ClientId, built.Text, send.AuthorMemberId, now,
            Kind: MessageKind.Template, DetailsJson: built.DetailsJson);
        await using var claim = await outbound.ClaimAsync(draft, ct);
        if (await OutboundGuards.TakeInFlightAsync(claim) is { } inFlight)
        {
            return new SupportReplySendResult.Sent(inFlight.Id, conversation.Id);
        }

        var result = await meta.SendTemplateAsync(
            sender, SendTarget.ForTemplate(conversation.UserId, conversation.WaId), built.Send,
            SendMessageHandler.CallbackPrefix + claim.MessageId.ToString("D"), CancellationToken.None);
        return await SettleAsync(send.TenantId, conversation, claim, result);
    }

    private async Task<SupportReplySendResult?> AlreadySentAsync(Guid conversationId, Guid clientId, CancellationToken ct) =>
        await outbound.FindByClientIdAsync(conversationId, clientId, ct) is { Status: not MessageStatus.Failed } sent
            ? new SupportReplySendResult.Sent(sent.Id, conversationId)
            : null;

    /// <summary>La tabla de respuestas de Meta, con <c>seen: 0</c>: nadie leyó el hilo para responder desde Soporte.</summary>
    private async Task<SupportReplySendResult> SettleAsync(Guid tenantId, ConversationRow conversation, IOutboundClaim claim, SendResult result)
    {
        try
        {
            await new OutboundSettlement(repository, connections, notifier, receipts, clock)
                .SettleAsync(tenantId, conversation.ConnectionId, claim, result, clock.UtcNow, metaMediaId: null, afterRollback: null, seen: 0);
        }
        catch (MessagingDomainException exception) when (exception.Code == MessagingErrorCodes.ConnectionUnavailable)
        {
            // Meta rechazó la credencial o el número: la fila no se guardó y la conexión ya quedó NeedsAttention.
            return Rejected(SupportReplyReasons.ConnectionUnavailable);
        }
        catch (MessagingDomainException exception) when (exception.Code == MessagingErrorCodes.WindowClosed)
        {
            return new SupportReplySendResult.Failed(claim.MessageId, SupportReplyReasons.WindowClosed);
        }
        catch (MessagingDomainException)
        {
            // Failed: la fila queda, para que el hilo y el ticket muestren qué no salió.
            return new SupportReplySendResult.Failed(claim.MessageId, SupportReplyReasons.SendFailed);
        }

        return new SupportReplySendResult.Sent(claim.MessageId, conversation.Id);
    }

    private static SupportReplySendResult.Rejected Rejected(string reason) => new(reason);
}
