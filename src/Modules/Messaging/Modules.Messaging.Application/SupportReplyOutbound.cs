using System.Security.Cryptography;
using BuildingBlocks.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>Soporte fase 2 (plan E13 y E19): una respuesta de un ticket en el hilo de su conversación, dentro de la ventana de 24 h.
/// <c>ClientId</c> es el id de la fila de respuesta de Soporte: un reintento con el mismo no vuelve a mandar nada. Con <c>UploadId</c>
/// sale el adjunto que el asesor subió por la sesión de subida de Mensajería para esa conversación (sólo WhatsApp), y <c>Text</c> va
/// como su pie;
/// <c>FileName</c> es el nombre que vio al elegir el archivo, del que sale la extensión de la clave del staging.</summary>
public sealed record SupportThreadReplySend(
    Guid TenantId, Guid ConversationId, Guid ClientId, Guid AuthorMemberId, string Text, Guid? UploadId, string? FileName = null);

/// <summary>Soporte fase 2 (plan E13 y E14): una respuesta de un ticket por la plantilla «Aviso de respuesta». A una conversación existente
/// (<c>ConversationId</c>) o, sin conversación, a <c>WaId</c> (sólo dígitos, con indicativo) por la conexión <c>ConnectionId</c> que eligió el
/// asesor, abriendo la conversación de ese número. <c>RecipientName</c> llena <c>nombre_contacto</c>, <c>TicketNumber</c> el
/// <c>radicado</c> y <c>Text</c> la <c>respuesta</c>.</summary>
public sealed record SupportTemplateReplySend(
    Guid TenantId, Guid? ConversationId, Guid? ConnectionId, string? WaId, Guid? CustomerId,
    string RecipientName, Guid ClientId, Guid AuthorMemberId, string TicketNumber, string Text);

/// <summary>Soporte fase 4b (H10): la respuesta formal por el hilo, dentro de la ventana de 24 h, con el PDF. En WhatsApp sale un solo
/// documento con <c>Text</c> como pie (<c>ClientId</c>); en Instagram, que no lleva pie en un adjunto, sale el texto (<c>ClientId</c>) y
/// después el archivo (<c>DocumentClientId</c>, guardado en el documento de Soporte para que un reintento lo repita). <c>FileName</c> es el
/// nombre con que lo ve el contacto.</summary>
public sealed record SupportThreadDocumentSend(Guid TenantId, Guid ConversationId, Guid ClientId, Guid DocumentClientId,
    Guid AuthorMemberId, string Text, ReadOnlyMemory<byte> Document, string FileName);

/// <summary>Soporte fase 4b (H10): la respuesta formal por la plantilla «Respuesta formal», con el PDF como encabezado de documento. Los
/// destinos son los de <see cref="SupportTemplateReplySend"/>: la conversación existente o, sin ella, el número por la conexión elegida.</summary>
public sealed record SupportFormalTemplateSend(Guid TenantId, Guid? ConversationId, Guid? ConnectionId, string? WaId, Guid? CustomerId,
    string RecipientName, Guid ClientId, Guid AuthorMemberId, string TicketNumber, string Text, ReadOnlyMemory<byte> Document, string FileName);

/// <summary>Soporte fase 4b (H16): la encuesta de satisfacción por su plantilla, a la conversación de WhatsApp del ticket. Los botones viajan
/// sin parámetros: Meta devuelve el toque con su texto (V-1).</summary>
public sealed record SupportSurveyTemplateSend(Guid TenantId, Guid ConversationId, Guid ClientId, Guid AuthorMemberId,
    string RecipientName, string TicketNumber);

public abstract record SupportReplySendResult
{
    /// <summary><paramref name="DocumentMessageId"/> (fase 4b) es el mensaje que lleva el PDF de una respuesta formal por el hilo: el mismo
    /// <paramref name="MessageId"/> en WhatsApp (un documento con pie) y el segundo mensaje en Instagram (el texto y después el archivo).
    /// <c>null</c> en todo otro envío.</summary>
    public sealed record Sent(Guid MessageId, Guid ConversationId, Guid? DocumentMessageId = null) : SupportReplySendResult;

    /// <summary>Antes de enviar, sin fila en el hilo ni llamada a Meta (salvo <c>connection_unavailable</c> por credencial rechazada al
    /// enviar, que tampoco deja fila). <paramref name="Reason"/> es uno de <see cref="SupportReplyReasons"/>: <c>window_closed</c>,
    /// <c>contact_blocked</c>, <c>template_not_approved</c>, <c>template_text_invalid</c>, <c>recipient_invalid</c>, <c>text_invalid</c>, <c>connection_unavailable</c>,
    /// <c>conversation_not_found</c>, <c>attachment_unavailable</c>, <c>channel_unsupported</c>. En la respuesta formal (fase 4b) también
    /// <c>send_failed</c>: el PDF no subió a Meta o no se pudo guardar en R2, y no salió nada.</summary>
    public sealed record Rejected(string Reason) : SupportReplySendResult;

    /// <summary>Meta lo rechazó o no lo confirmó: la fila queda <c>Failed</c> en el hilo. <paramref name="Code"/> es <c>window_closed</c>
    /// (Meta cerró la ventana entre la lectura y el envío) o <c>send_failed</c>. En la respuesta formal por Instagram, un archivo que no
    /// sale después de que el texto ya salió es siempre <c>send_failed</c> (con el id del archivo si quedó fila): el texto queda enviado.</summary>
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

    /// <summary>El adjunto no existe en el staging de quien responde para esa conversación (de otra conversación, vencido o ya usado),
    /// falta su nombre, la conversación es de Instagram (no lleva pie: el texto de la respuesta se perdería), el canal no acepta ese
    /// tipo o ese tamaño, su contenido no es el que dice ser, es un audio (no lleva pie y la
    /// respuesta siempre tiene texto) o no se pudo guardar.</summary>
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

    /// <summary>Dentro de la ventana, el texto (o el adjunto con el texto como pie); fuera, <c>Rejected(window_closed)</c> sin fila.</summary>
    Task<SupportReplySendResult> SendThreadAsync(SupportThreadReplySend send, CancellationToken ct);

    Task<SupportReplySendResult> SendTemplateAsync(SupportTemplateReplySend send, CancellationToken ct);

    /// <summary>Fase 4b: la respuesta formal por el hilo, con las mismas guardas que <see cref="SendThreadAsync"/>.</summary>
    Task<SupportReplySendResult> SendThreadDocumentAsync(SupportThreadDocumentSend send, CancellationToken ct);

    /// <summary>Fase 4b: la respuesta formal por la plantilla «Respuesta formal», con los pasos de <see cref="SendTemplateAsync"/>.</summary>
    Task<SupportReplySendResult> SendFormalTemplateAsync(SupportFormalTemplateSend send, CancellationToken ct);

    /// <summary>Fase 4b: la encuesta por su plantilla, sólo a una conversación de WhatsApp que ya existe.</summary>
    Task<SupportReplySendResult> SendSurveyTemplateAsync(SupportSurveyTemplateSend send, CancellationToken ct);
}

/// <summary>
/// Soporte fase 2 (plan E12): el envío de una respuesta de Soporte. El texto sigue a <see cref="BotOutbound"/> y la plantilla a
/// <see cref="BillingTemplateOutbound"/>: idempotente por <c>ClientId</c> (lo ya enviado se devuelve antes de mirar bloqueo o ventana, y dos
/// en vuelo se resuelven con el candado del reclamo), autor <c>AuthorMemberId</c> y sin <see cref="OutboundGuards"/>: lo autoriza Soporte con
/// su permiso, así que no exige la conversación <c>Open</c> ni que sea del asesor, no la toma, no cambia su estado ni su asignación y no resta
/// no leídos (<c>seen: 0</c>). El reclamo y la liquidación sí son los de siempre (<see cref="IOutboundMessages"/>,
/// <see cref="OutboundSettlement"/>), y la liquidación es la que apaga <c>history_managed</c>.
/// <para>
/// Fase 4b: la respuesta formal y la encuesta siguen las mismas reglas. El PDF sube a Meta antes del reclamo y queda en el R2 de Mensajería
/// con la clave del mensaje, como el documento de <see cref="DocumentTemplateOutbound"/>; las tres plantillas comparten el camino de
/// <see cref="SendTemplateCoreAsync"/>.
/// </para>
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
    ISupportFormalReplyTemplateReadiness formalTemplateReadiness,
    ISupportSurveyTemplateReadiness surveyTemplateReadiness,
    TemplateSenderValues senderValues,
    IMessagingMediaStore store,
    IMessagingUploadStaging staging,
    IMessagingImageCompressor compressor) : ISupportReplyOutbound
{
    private const string PdfMimeType = "application/pdf";

    // El multipart lleva un nombre genérico ASCII; el que ve el contacto viaja en el mensaje (como en DocumentTemplateOutbound).
    private const string GenericDocumentName = "document.pdf";

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

        var isInstagram = conversation.Channel == ConversationChannel.Instagram;
        if (!SendMessageValidator.IsAcceptableText(send.Text)
            || (isInstagram && send.Text.Trim().Length > SendMessageValidator.InstagramTextMaxLength)
            || (send.UploadId is not null && send.Text.Trim().Length > SendMediaMessageValidator.MaxCaptionLength))
        {
            return Rejected(SupportReplyReasons.TextInvalid);
        }

        // La ventana se mira antes de reclamar: un rechazo no deja fila.
        var now = clock.UtcNow;
        if (conversation.LastInboundAt is not { } lastInbound || lastInbound.Add(Conversation.WindowLength) <= now)
        {
            return Rejected(SupportReplyReasons.WindowClosed);
        }

        if (send.UploadId is { } uploadId)
        {
            return await SendThreadMediaAsync(send, conversation, uploadId, now, ct);
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

        return await SendTemplateCoreAsync(
            new TemplateTarget(send.TenantId, send.ConversationId, send.ConnectionId, send.WaId, send.CustomerId, send.RecipientName, send.ClientId,
                send.AuthorMemberId),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [SystemTemplateVariables.ContactName] = send.RecipientName,
                [SupportReplyTemplateDefinition.CaseNumberKey] = send.TicketNumber,
                [SupportReplyTemplateDefinition.ReplyKey] = send.Text.Trim(),
            },
            templateReadiness.FindApprovedAsync, document: null, ct);
    }

    public async Task<SupportReplySendResult> SendFormalTemplateAsync(SupportFormalTemplateSend send, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(send);
        var document = DocumentOf(send.Document, send.FileName);

        // 1. Las mismas reglas de Meta que «Aviso de respuesta»: el texto del canal es la variable respuesta.
        if (!TemplateParameterRules.IsValid(send.RecipientName) || !TemplateParameterRules.IsValid(send.TicketNumber))
        {
            return Rejected(SupportReplyReasons.RecipientInvalid);
        }

        if (!TemplateParameterRules.IsValid(send.Text))
        {
            return Rejected(SupportReplyReasons.TemplateTextInvalid);
        }

        return await SendTemplateCoreAsync(
            new TemplateTarget(send.TenantId, send.ConversationId, send.ConnectionId, send.WaId, send.CustomerId, send.RecipientName, send.ClientId,
                send.AuthorMemberId),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [SystemTemplateVariables.ContactName] = send.RecipientName,
                [SupportReplyTemplateDefinition.CaseNumberKey] = send.TicketNumber,
                [SupportReplyTemplateDefinition.ReplyKey] = send.Text.Trim(),
            },
            formalTemplateReadiness.FindApprovedAsync, document, ct);
    }

    public async Task<SupportReplySendResult> SendSurveyTemplateAsync(SupportSurveyTemplateSend send, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(send);
        if (!TemplateParameterRules.IsValid(send.RecipientName) || !TemplateParameterRules.IsValid(send.TicketNumber))
        {
            return Rejected(SupportReplyReasons.RecipientInvalid);
        }

        // Los botones son fijos y viajan sin parámetros: sólo el nombre y el radicado (más las fijas que use la fila).
        return await SendTemplateCoreAsync(
            new TemplateTarget(send.TenantId, send.ConversationId, null, null, null, send.RecipientName, send.ClientId, send.AuthorMemberId),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [SystemTemplateVariables.ContactName] = send.RecipientName,
                [SupportReplyTemplateDefinition.CaseNumberKey] = send.TicketNumber,
            },
            surveyTemplateReadiness.FindApprovedAsync, document: null, ct);
    }

    /// <summary>Lo que viaja a una plantilla de Soporte: la conversación existente o, sin ella, la conexión y el número para abrir una.</summary>
    private sealed record TemplateTarget(
        Guid TenantId, Guid? ConversationId, Guid? ConnectionId, string? WaId, Guid? CustomerId, string RecipientName, Guid ClientId,
        Guid AuthorMemberId);

    /// <summary>El PDF de la respuesta formal, ya en bytes, con el nombre que ve el contacto.</summary>
    private sealed record FormalDocument(byte[] Content, string FileName)
    {
        public string Sha256Hex { get; } = Convert.ToHexStringLower(SHA256.HashData(Content));
    }

    private static FormalDocument DocumentOf(ReadOnlyMemory<byte> content, string? fileName)
    {
        // Lo arma Soporte con su propio documento: sin bytes o sin nombre es un error de programación, no del asesor.
        if (content.IsEmpty || string.IsNullOrWhiteSpace(fileName) || Path.GetFileName(fileName.Trim()).Length == 0)
        {
            throw new ArgumentException("A formal reply document needs its content and its file name.", nameof(content));
        }

        return new FormalDocument(content.ToArray(), Path.GetFileName(fileName.Trim()));
    }

    /// <summary>
    /// Los pasos 2 a 6 de una plantilla de Soporte, los mismos para «Aviso de respuesta», «Respuesta formal» y la encuesta: la conversación o el
    /// número (con lo ya enviado antes de mirar la plantilla o la conexión), la fila aprobada de su tipo, los valores, el remitente, la
    /// conversación del número, el reclamo y el envío. Con <paramref name="document"/>, el PDF sube a Meta antes de abrir la conversación (si
    /// falla no queda fila ni conversación), se guarda en R2 con la clave del reclamo y viaja como encabezado de documento.
    /// </summary>
    private async Task<SupportReplySendResult> SendTemplateCoreAsync(
        TemplateTarget send,
        Dictionary<string, string> values,
        Func<Guid, Guid, CancellationToken, Task<ApprovedTemplate?>> findApproved,
        FormalDocument? document,
        CancellationToken ct)
    {
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

        // 3. La fila aprobada de su tipo en esa conexión (nunca la constante: puede ser una _v2 con el texto del tenant).
        if (await findApproved(send.TenantId, connectionId, ct) is not { } template)
        {
            return Rejected(SupportReplyReasons.TemplateNotApproved);
        }

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

        // 4b. Fase 4b: el PDF sube a Meta antes de abrir la conversación y antes del reclamo, como DocumentTemplateOutbound.
        string? mediaId = null;
        if (document is not null)
        {
            MessagingGraphResult<string> upload;
            using (var content = new MemoryStream(document.Content, writable: false))
            {
                upload = await meta.UploadMediaAsync(sender, content, document.Content.Length, PdfMimeType, GenericDocumentName, ct);
            }

            if (upload is not { Succeeded: true, Value: { Length: > 0 } uploaded })
            {
                // Un 4xx con 190/133010 dice que la credencial o el número ya no sirven: la conexión pasa a NeedsAttention.
                if (upload.Failure is { HttpStatus: >= 400 and < 500, Code: MetaErrorCodes.TokenExpired or MetaErrorCodes.NumberUnregistered } rejected)
                {
                    await ReportRejectedAsync(send.TenantId, connectionId, MetaErrorCodes.RejectedReason(rejected.Code!.Value));
                    return Rejected(SupportReplyReasons.ConnectionUnavailable);
                }

                return Rejected(SupportReplyReasons.SendFailed);
            }

            mediaId = uploaded;
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
            Kind: MessageKind.Template, DetailsJson: built.DetailsJson,
            Media: document is null ? null : new OutboundMedia(PdfMimeType, document.Content.Length, document.Sha256Hex, document.FileName));
        await using var claim = await outbound.ClaimAsync(draft, ct);
        if (await OutboundGuards.TakeInFlightAsync(claim) is { } inFlight)
        {
            return new SupportReplySendResult.Sent(inFlight.Id, conversation.Id);
        }

        var target = SendTarget.ForTemplate(conversation.UserId, conversation.WaId);
        var callback = SendMessageHandler.CallbackPrefix + claim.MessageId.ToString("D");
        if (document is null)
        {
            var result = await meta.SendTemplateAsync(sender, target, built.Send, callback, CancellationToken.None);
            return await SettleAsync(send.TenantId, conversation, claim, result);
        }

        // La copia propia de Mensajería, con la clave del reclamo: el reintento de un Failed reutiliza el messageId y sobrescribe el objeto.
        var key = MessagingMediaKeys.For(send.TenantId, claim.MessageId);
        if (!await TryStoreAsync(key, document.Content, ct))
        {
            // No se envió nada y la fila no queda.
            await claim.RollbackAsync(CancellationToken.None);
            return Rejected(SupportReplyReasons.SendFailed);
        }

        // Desde acá todo va con CancellationToken.None: si Meta ya aceptó, cortar borraría la fila y el reintento mandaría dos veces.
        try
        {
            var result = await meta.SendTemplateAsync(
                sender, target, built.Send with { Header = TemplateHeader.DocumentById(mediaId!, document.FileName) }, callback,
                CancellationToken.None);
            return await SettleAsync(send.TenantId, conversation, claim, result, mediaId, claim.Inserted ? () => DiscardAsync(key) : null);
        }
        catch (Exception exception) when (claim.Inserted && exception is not MessagingDomainException)
        {
            await DiscardAsync(key);
            throw;
        }
    }

    public async Task<SupportReplySendResult> SendThreadDocumentAsync(SupportThreadDocumentSend send, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(send);
        var document = DocumentOf(send.Document, send.FileName);
        if (send.ClientId == send.DocumentClientId)
        {
            throw new ArgumentException("The text and the document of a formal reply need two distinct client ids.", nameof(send));
        }

        var conversation = await conversations.FindAsync(send.TenantId, send.ConversationId, ct);
        if (conversation is null)
        {
            return Rejected(SupportReplyReasons.ConversationNotFound);
        }

        return conversation.Channel == ConversationChannel.Instagram
            ? await SendInstagramDocumentAsync(send, conversation, document, ct)
            : await SendWhatsAppDocumentAsync(send, conversation, document, ct);
    }

    /// <summary>WhatsApp: un solo documento con el texto como pie, por el camino del adjunto de <see cref="SendThreadMediaAsync"/> con los
    /// bytes ya en memoria (la subida a Meta antes del reclamo; una subida fallida entra a la tabla del envío y deja la fila Failed).</summary>
    private async Task<SupportReplySendResult> SendWhatsAppDocumentAsync(
        SupportThreadDocumentSend send, ConversationRow conversation, FormalDocument document, CancellationToken ct)
    {
        if (await AlreadySentAsync(conversation.Id, send.ClientId, ct) is { } sent)
        {
            return sent with { DocumentMessageId = sent.MessageId };
        }

        if (conversation.BlockedAt is not null)
        {
            return Rejected(SupportReplyReasons.ContactBlocked);
        }

        if (!SendMessageValidator.IsAcceptableText(send.Text) || send.Text.Trim().Length > SendMediaMessageValidator.MaxCaptionLength)
        {
            return Rejected(SupportReplyReasons.TextInvalid);
        }

        var now = clock.UtcNow;
        if (!IsWindowOpen(conversation, now))
        {
            return Rejected(SupportReplyReasons.WindowClosed);
        }

        if ((await connections.ResolveSenderWithReasonAsync(send.TenantId, conversation.ConnectionId, ct)).Sender is not { } sender)
        {
            return Rejected(SupportReplyReasons.ConnectionUnavailable);
        }

        MessagingGraphResult<string> upload;
        using (var content = new MemoryStream(document.Content, writable: false))
        {
            upload = await meta.UploadMediaAsync(sender, content, document.Content.Length, PdfMimeType, GenericDocumentName, ct);
        }

        var caption = send.Text.Trim();
        var draft = new OutboundDraft(
            Guid.CreateVersion7(), conversation.Id, send.TenantId, conversation.ConnectionId, send.ClientId, Text: null, send.AuthorMemberId, now,
            Kind: MessageKind.Document, Media: new OutboundMedia(PdfMimeType, document.Content.Length, document.Sha256Hex, document.FileName),
            Caption: caption);
        await using var claim = await outbound.ClaimAsync(draft, ct);
        if (await OutboundGuards.TakeInFlightAsync(claim) is { } inFlight)
        {
            return new SupportReplySendResult.Sent(inFlight.Id, conversation.Id, inFlight.Id);
        }

        var key = MessagingMediaKeys.For(send.TenantId, claim.MessageId);
        if (!await TryStoreAsync(key, document.Content, ct))
        {
            await claim.RollbackAsync(CancellationToken.None);
            return Rejected(SupportReplyReasons.SendFailed);
        }

        // Desde acá todo va con CancellationToken.None: si Meta ya aceptó, cortar borraría la fila y el reintento mandaría dos veces.
        SupportReplySendResult settled;
        try
        {
            var mediaId = upload is { Succeeded: true, Value: { Length: > 0 } uploaded } ? uploaded : null;
            var result = mediaId is null
                ? SendAudioMessageHandler.FromUploadFailure(upload.Failure)
                : await meta.SendDocumentAsync(
                    sender, SendTarget.For(conversation.UserId, conversation.WaId), mediaId, document.FileName, caption,
                    SendMessageHandler.CallbackPrefix + claim.MessageId.ToString("D"), null, CancellationToken.None);
            settled = await SettleAsync(
                send.TenantId, conversation, claim, result, mediaId, claim.Inserted ? () => DiscardAsync(key) : null);
        }
        catch (Exception exception) when (claim.Inserted && exception is not MessagingDomainException)
        {
            await DiscardAsync(key);
            throw;
        }

        return settled is SupportReplySendResult.Sent done ? done with { DocumentMessageId = done.MessageId } : settled;
    }

    /// <summary>
    /// Instagram: el texto (<c>ClientId</c>) y después el archivo (<c>DocumentClientId</c>) por una URL firmada de R2, como el adjunto del
    /// asesor (spec 2026-10-14 decisión 12). Cada mensaje es idempotente por su clientId: un reintento salta lo que ya salió. Las guardas
    /// (bloqueo, texto, ventana) se miran sólo mientras no haya salido nada; un archivo que no sale después del texto es
    /// <c>Failed(send_failed)</c> y el texto queda enviado.
    /// </summary>
    private async Task<SupportReplySendResult> SendInstagramDocumentAsync(
        SupportThreadDocumentSend send, ConversationRow conversation, FormalDocument document, CancellationToken ct)
    {
        var text = await AlreadySentAsync(conversation.Id, send.ClientId, ct);
        if (text is not null && await AlreadySentAsync(conversation.Id, send.DocumentClientId, ct) is { } file)
        {
            return new SupportReplySendResult.Sent(text.MessageId, conversation.Id, file.MessageId);
        }

        if (text is null)
        {
            if (conversation.BlockedAt is not null)
            {
                return Rejected(SupportReplyReasons.ContactBlocked);
            }

            if (!SendMessageValidator.IsAcceptableText(send.Text) || send.Text.Trim().Length > SendMessageValidator.InstagramTextMaxLength)
            {
                return Rejected(SupportReplyReasons.TextInvalid);
            }

            if (!IsWindowOpen(conversation, clock.UtcNow))
            {
                return Rejected(SupportReplyReasons.WindowClosed);
            }

            // Mismo camino que una respuesta de texto: sin conexión, ventana cerrada o Meta rechazando, nada más sale.
            var first = await SendThreadAsync(
                new SupportThreadReplySend(send.TenantId, conversation.Id, send.ClientId, send.AuthorMemberId, send.Text, UploadId: null), ct);
            if (first is not SupportReplySendResult.Sent textSent)
            {
                return first;
            }

            text = textSent;
        }

        // El texto ya salió: desde acá nada es un rechazo sin envío.
        if (conversation.BlockedAt is not null
            || await connections.ResolveInstagramSenderAsync(send.TenantId, conversation.ConnectionId, ct) is not { } igSender)
        {
            return new SupportReplySendResult.Failed(null, SupportReplyReasons.SendFailed);
        }

        var draft = new OutboundDraft(
            Guid.CreateVersion7(), conversation.Id, send.TenantId, conversation.ConnectionId, send.DocumentClientId, Text: null, send.AuthorMemberId,
            clock.UtcNow, Kind: MessageKind.Document,
            Media: new OutboundMedia(PdfMimeType, document.Content.Length, document.Sha256Hex, document.FileName));
        await using var claim = await outbound.ClaimAsync(draft, ct);
        if (await OutboundGuards.TakeInFlightAsync(claim) is { } inFlight)
        {
            return new SupportReplySendResult.Sent(text.MessageId, conversation.Id, inFlight.Id);
        }

        var key = MessagingMediaKeys.For(send.TenantId, claim.MessageId);
        if (!await TryStoreAsync(key, document.Content, ct))
        {
            await claim.RollbackAsync(CancellationToken.None);
            return new SupportReplySendResult.Failed(null, SupportReplyReasons.SendFailed);
        }

        SupportReplySendResult settled;
        try
        {
            var result = await SendInstagramFileAsync(igSender, conversation.UserId!, key);
            settled = await SettleAsync(send.TenantId, conversation, claim, result, afterRollback: claim.Inserted ? () => DiscardAsync(key) : null);
        }
        catch (Exception exception) when (claim.Inserted && exception is not MessagingDomainException)
        {
            await DiscardAsync(key);
            throw;
        }

        return settled switch
        {
            SupportReplySendResult.Sent done => new SupportReplySendResult.Sent(text.MessageId, conversation.Id, done.MessageId),
            SupportReplySendResult.Failed failed => new SupportReplySendResult.Failed(failed.MessageId, SupportReplyReasons.SendFailed),
            _ => new SupportReplySendResult.Failed(null, SupportReplyReasons.SendFailed),
        };
    }

    /// <summary>La URL firmada y el envío, como el adjunto del asesor: si R2 no firma, no se envió nada y la fila queda Failed.</summary>
    private async Task<SendResult> SendInstagramFileAsync(InstagramSender sender, string recipientId, string key)
    {
        Uri url;
        try
        {
            url = await store.CreateDownloadUrlAsync(key, SendMediaMessageHandler.InstagramDownloadLifetime, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new SendResult(SendOutcome.NotSent, null, null, null);
        }

        return await instagram.SendAttachmentAsync(
            sender, recipientId, OutboundMediaPolicy.InstagramAttachmentType(OutboundMediaFormat.Pdf), url, CancellationToken.None);
    }

    private static bool IsWindowOpen(ConversationRow conversation, DateTimeOffset now) =>
        conversation.LastInboundAt is { } lastInbound && lastInbound.Add(Conversation.WindowLength) > now;

    private async Task<bool> TryStoreAsync(string key, byte[] content, CancellationToken ct)
    {
        try
        {
            using var stream = new MemoryStream(content, writable: false);
            await store.UploadAsync(key, stream, content.Length, PdfMimeType, ct);
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    // Best effort: la respuesta ya es connection_unavailable, y el próximo envío vuelve a recibir 190/133010 y reintenta el reporte.
    private async Task ReportRejectedAsync(Guid tenantId, Guid connectionId, string failureCode)
    {
        try
        {
            await connections.ReportRejectedAsync(tenantId, connectionId, failureCode, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Ver comentario.
        }
    }

    /// <summary>
    /// Tarea 11 (plan E19): el adjunto de la respuesta, por el camino de <see cref="SendMediaMessageHandler"/> sin sus guardas de bandeja.
    /// El staging es el de quien responde para esta conversación (la clave lleva tenant, conversación, membresía, <c>uploadId</c> y
    /// extensión), así que un <c>uploadId</c> de otra conversación, vencido o ya usado no existe: <c>attachment_unavailable</c>, como
    /// cualquier problema del archivo, todo antes del reclamo y sin fila. Lo lento (leer, comprobar, comprimir, subir a Meta) va antes del
    /// reclamo; dentro, la escritura en R2 y el envío. <c>Sent</c> consume el staging; <c>Failed</c> lo conserva para el reintento.
    /// </summary>
    private async Task<SupportReplySendResult> SendThreadMediaAsync(
        SupportThreadReplySend send, ConversationRow conversation, Guid uploadId, DateTimeOffset now, CancellationToken ct)
    {
        // Ruling del controlador (revisión del bloque D): Instagram no lleva pie en un adjunto, así que el texto de la respuesta, que es
        // lo que el ticket registra, nunca le llegaría al contacto. Sólo WhatsApp.
        if (conversation.Channel != ConversationChannel.WhatsApp || string.IsNullOrWhiteSpace(send.FileName))
        {
            return Rejected(SupportReplyReasons.AttachmentUnavailable);
        }

        // Un audio no lleva pie (spec 2026-10-20 D7) y la respuesta de Soporte siempre tiene texto: no se manda uno que se vería distinto.
        var fileName = Path.GetFileName(send.FileName.Trim());
        var extension = OutboundMediaPolicy.ExtensionOf(fileName);
        var channel = conversation.Channel;
        if (OutboundMediaPolicy.FormatOf(extension) is not { } declared
            || !OutboundMediaPolicy.IsAllowed(declared, channel)
            || !OutboundMediaPolicy.AcceptsCaption(declared))
        {
            return Rejected(SupportReplyReasons.AttachmentUnavailable);
        }

        var stagingKey = MessagingMediaKeys.Staging(send.TenantId, conversation.Id, send.AuthorMemberId, uploadId, extension);
        var staged = await staging.StatAsync(stagingKey, ct);
        if (staged is null)
        {
            // Dos requests con el mismo clientId: el primero ya envió y consumió el staging. Gana la idempotencia.
            return (SupportReplySendResult?)await AlreadySentAsync(conversation.Id, send.ClientId, ct)
                ?? Rejected(SupportReplyReasons.AttachmentUnavailable);
        }

        if (staged.SizeBytes <= 0 || staged.SizeBytes > OutboundMediaPolicy.MaxBytesOf(declared, channel)!.Value)
        {
            await DiscardAsync(stagingKey);
            return Rejected(SupportReplyReasons.AttachmentUnavailable);
        }

        if ((await connections.ResolveSenderWithReasonAsync(send.TenantId, conversation.ConnectionId, ct)).Sender is not { } sender)
        {
            return Rejected(SupportReplyReasons.ConnectionUnavailable);
        }

        PreparedOutboundMedia prepared;
        try
        {
            prepared = await new OutboundMediaPreparer(meta, store, compressor).PrepareAsync(
                sender, stagingKey, staged.SizeBytes, fileName, extension, () => DiscardAsync(stagingKey), ct);
        }
        catch (MessagingDomainException)
        {
            // El contenido no es el que dice la extensión, la imagen no se lee o el objeto ya no está: nada salió.
            return Rejected(SupportReplyReasons.AttachmentUnavailable);
        }

        var caption = send.Text.Trim();
        var kind = OutboundMediaPolicy.KindOf(declared);
        var draft = new OutboundDraft(
            Guid.CreateVersion7(), conversation.Id, send.TenantId, conversation.ConnectionId, send.ClientId, Text: null, send.AuthorMemberId, now,
            Kind: kind, Media: new OutboundMedia(prepared.MimeType, prepared.SizeBytes, prepared.Sha256Hex, prepared.FileName), Caption: caption);
        await using var claim = await outbound.ClaimAsync(draft, ct);
        if (await OutboundGuards.TakeInFlightAsync(claim) is { } inFlight)
        {
            return new SupportReplySendResult.Sent(inFlight.Id, conversation.Id);
        }

        // R2 con la clave del reclamo: el reintento de un Failed reutiliza el messageId y sobrescribe el mismo objeto.
        var finalKey = MessagingMediaKeys.For(send.TenantId, claim.MessageId);
        try
        {
            if (prepared.Compressed is { } bytes)
            {
                using var content = new MemoryStream(bytes, writable: false);
                await store.UploadAsync(finalKey, content, bytes.Length, OutboundMediaPreparer.ImageMimeType, ct);
            }
            else
            {
                await staging.PromoteAsync(stagingKey, finalKey, staged.ETag, ct);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException && !ct.IsCancellationRequested)
        {
            // No se envió nada y la fila no queda: reintentar con el mismo clientId es seguro (el staging sigue ahí).
            await claim.RollbackAsync(CancellationToken.None);
            return Rejected(SupportReplyReasons.AttachmentUnavailable);
        }

        // Desde acá todo va con CancellationToken.None: si Meta ya aceptó, cortar borraría la fila y el reintento mandaría dos veces.
        SupportReplySendResult settled;
        try
        {
            var mediaId = prepared.Upload is { Succeeded: true, Value: { Length: > 0 } uploaded } ? uploaded : null;
            var callback = SendMessageHandler.CallbackPrefix + claim.MessageId.ToString("D");
            var recipient = SendTarget.For(conversation.UserId, conversation.WaId);
            var result = mediaId is null
                ? SendAudioMessageHandler.FromUploadFailure(prepared.Upload?.Failure)
                : kind switch
                {
                    MessageKind.Image => await meta.SendImageAsync(sender, recipient, mediaId, caption, callback, null, CancellationToken.None),
                    MessageKind.Video => await meta.SendVideoAsync(sender, recipient, mediaId, caption, callback, null, CancellationToken.None),
                    _ => await meta.SendDocumentAsync(sender, recipient, mediaId, prepared.FileName, caption, callback, null, CancellationToken.None),
                };

            settled = await SettleAsync(
                send.TenantId, conversation, claim, result, mediaId, claim.Inserted ? () => DiscardAsync(finalKey) : null);
        }
        // Como en el adjunto del asesor (SendMediaMessageHandler): una falla inesperada con una fila nueva deja en R2 un objeto que ninguna
        // fila sirve; se borra y la excepción sigue. En el reintento de un Failed el objeto es el que esa fila ya sirve, y se queda.
        catch (Exception exception) when (claim.Inserted && exception is not MessagingDomainException)
        {
            await DiscardAsync(finalKey);
            throw;
        }

        if (settled is SupportReplySendResult.Sent)
        {
            await DiscardAsync(stagingKey);
        }

        return settled;
    }

    // Best effort, como en el adjunto del asesor: lo que no se borre lo limpia el lifecycle del bucket.
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

    private async Task<SupportReplySendResult.Sent?> AlreadySentAsync(Guid conversationId, Guid clientId, CancellationToken ct) =>
        await outbound.FindByClientIdAsync(conversationId, clientId, ct) is { Status: not MessageStatus.Failed } sent
            ? new SupportReplySendResult.Sent(sent.Id, conversationId)
            : null;

    /// <summary>La tabla de respuestas de Meta, con <c>seen: 0</c>: nadie leyó el hilo para responder desde Soporte.</summary>
    private async Task<SupportReplySendResult> SettleAsync(
        Guid tenantId, ConversationRow conversation, IOutboundClaim claim, SendResult result, string? metaMediaId = null,
        Func<Task>? afterRollback = null)
    {
        try
        {
            await new OutboundSettlement(repository, connections, notifier, receipts, clock)
                .SettleAsync(tenantId, conversation.ConnectionId, claim, result, clock.UtcNow, metaMediaId, afterRollback, seen: 0);
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
