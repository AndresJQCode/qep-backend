using Modules.Support.Domain;

namespace Modules.Support.Application;

/// <summary>Cómo y a dónde sale una respuesta: la entrega y, según ella, la conversación, la conexión elegida y el <c>wa_id</c> del
/// celular (sólo para la plantilla a un ticket sin conversación, plan E14).</summary>
internal sealed record SupportReplyRoute(
    SupportReplyDelivery Delivery, Guid? ConversationId, Guid? ConnectionId, string? WaId);

/// <summary>Lo que el hilo del ticket permite ahora: la ruta, o el motivo para la pantalla y el código para el 422.</summary>
internal sealed record SupportThreadAvailability(
    SupportReplyRoute? Route, string? Reason, string? ErrorCode, DateTimeOffset? WindowClosesAt);

/// <summary>Las reglas de la respuesta que comparten <c>reply-options</c> y el envío (plan E13 a E15), para que la pantalla prometa lo
/// mismo que el envío hace. Los motivos son de <see cref="SupportChannelReasons"/> (Tarea 8a: un solo vocabulario).</summary>
internal static class SupportReplyRules
{
    /// <summary>
    /// Plan E13: dentro de la ventana de 24 h, texto; fuera, en WhatsApp la plantilla «Aviso de respuesta» a la misma conversación si su
    /// conexión la tiene aprobada, y en Instagram nada (no tiene plantillas). Un contacto bloqueado no recibe nada, y por una conexión que
    /// no está activa no sale nada (<c>connection_unavailable</c>). Quien llama ya comprobó el módulo de Mensajería. Fase 4b (H10): con
    /// <paramref name="formal"/>, fuera de la ventana la plantilla es «Respuesta formal», que se aprueba aparte (Q-1).
    /// </summary>
    public static async Task<SupportThreadAvailability> ThreadAsync(
        ISupportReplyChannels channels, SupportTicket ticket, DateTimeOffset now, CancellationToken cancellationToken, bool formal = false)
    {
        if (ticket.ConversationId is not { } conversationId)
        {
            return new SupportThreadAvailability(null, SupportChannelReasons.NoConversation, SupportErrorCodes.NoConversation, null);
        }

        var thread = await channels.FindThreadAsync(ticket.TenantId, conversationId, cancellationToken);
        if (thread is null)
        {
            return new SupportThreadAvailability(
                null, SupportChannelReasons.NoConversation, SupportErrorCodes.ConversationNotFound, null);
        }

        if (thread.Blocked)
        {
            return new SupportThreadAvailability(
                null, SupportChannelReasons.ContactBlocked, SupportErrorCodes.ReplyContactBlocked, thread.WindowClosesAt);
        }

        if (!thread.ConnectionOperational)
        {
            return new SupportThreadAvailability(
                null, SupportChannelReasons.ConnectionUnavailable, SupportErrorCodes.ReplyConnectionUnavailable, thread.WindowClosesAt);
        }

        if (thread.WindowClosesAt is { } closesAt && closesAt > now)
        {
            return new SupportThreadAvailability(
                new SupportReplyRoute(SupportReplyDelivery.Text, conversationId, null, null), null, null, closesAt);
        }

        if (thread.Channel == SupportConversationRef.Instagram)
        {
            return new SupportThreadAvailability(
                null, SupportChannelReasons.WindowClosed, SupportErrorCodes.ReplyWindowClosed, thread.WindowClosesAt);
        }

        var approved = formal
            ? await channels.IsFormalTemplateApprovedAsync(ticket.TenantId, thread.ConnectionId, cancellationToken)
            : await channels.IsTemplateApprovedAsync(ticket.TenantId, thread.ConnectionId, cancellationToken);
        return approved
            ? new SupportThreadAvailability(
                new SupportReplyRoute(SupportReplyDelivery.Template, conversationId, null, null), null, null, thread.WindowClosesAt)
            : new SupportThreadAvailability(
                null, SupportChannelReasons.TemplateNotApproved, SupportErrorCodes.ReplyTemplateNotApproved, thread.WindowClosesAt);
    }

    /// <summary>El código de Soporte de cada motivo con que un canal rechaza un envío (ruling C-R1).</summary>
    public static string CodeFor(string reason) => reason switch
    {
        SupportChannelReasons.WindowClosed => SupportErrorCodes.ReplyWindowClosed,
        SupportChannelReasons.ContactBlocked => SupportErrorCodes.ReplyContactBlocked,
        SupportChannelReasons.TemplateNotApproved => SupportErrorCodes.ReplyTemplateNotApproved,
        SupportChannelReasons.TemplateTextInvalid or SupportChannelReasons.TextInvalid => SupportErrorCodes.ReplyTemplateTextInvalid,
        SupportChannelReasons.RecipientInvalid => SupportErrorCodes.ReplyRecipientInvalid,
        SupportChannelReasons.ChannelUnsupported => SupportErrorCodes.ReplyChannelUnsupported,
        SupportChannelReasons.ConnectionUnavailable => SupportErrorCodes.ReplyConnectionUnavailable,
        SupportChannelReasons.AttachmentUnavailable => SupportErrorCodes.ReplyAttachmentUnavailable,
        SupportChannelReasons.ConversationNotFound => SupportErrorCodes.ConversationNotFound,
        SupportChannelReasons.CustomerNotFound => SupportErrorCodes.CustomerNotFound,
        // Revisión del Bloque B: el PDF de la respuesta formal no subió a Meta o no se guardó en R2, y no salió nada.
        SupportChannelReasons.SendFailed => SupportErrorCodes.ReplySendFailed,
        // Un motivo nuevo de Mensajería que todavía no tiene código propio: genérico, nunca uno que mande a mirar otra cosa.
        _ => SupportErrorCodes.ReplyUnavailable,
    };
}

/// <summary>
/// Tarea 11 (plan E19): el adjunto de una respuesta se sube por la sesión de subida de Mensajería y sale por su camino. Copias de
/// Mensajería, que Soporte no ve, amarradas por <c>SupportReplyParityTests</c> (Bootstrapper.UnitTests): el largo del nombre
/// (<c>CreateMediaUploadSessionValidator.MaxFileNameLength</c>), el del pie, que es el texto de la respuesta
/// (<c>SendMediaMessageValidator.MaxCaptionLength</c>), y el permiso que exige la sesión de subida
/// (<c>MessagingPermissions.ConversationManage</c>): sin él la pantalla no ofrece adjuntar.
/// </summary>
public static class SupportReplyAttachmentRules
{
    public const int FileNameMaxLength = 256;

    public const int CaptionMaxLength = 1024;

    public const string UploadPermission = "messaging.conversation.manage";
}

/// <summary>
/// Las reglas del texto de una respuesta que dependen del canal. Son copias de Mensajería, que Soporte no ve: el valor de un parámetro de
/// plantilla de Meta (<c>TemplateParameterRules.IsValid</c>: de 1 a 1 024, sin saltos de línea ni tabulaciones ni más de cuatro espacios
/// seguidos) y el tope de Instagram (<c>SendMessageValidator.InstagramTextMaxLength</c>). Las amarra <c>SupportReplyParityTests</c>
/// (Bootstrapper.UnitTests): si cambian allá, la prueba se pone roja.
/// </summary>
public static class SupportReplyTextRules
{
    public const int TemplateValueMaxLength = 1024;

    public const int MaxConsecutiveSpaces = 4;

    public const int InstagramTextMaxLength = 1000;

    public static bool FitsTemplate(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= TemplateValueMaxLength
        && !value.Contains('\n', StringComparison.Ordinal)
        && !value.Contains('\r', StringComparison.Ordinal)
        && !value.Contains('\t', StringComparison.Ordinal)
        && !value.Contains(new string(' ', MaxConsecutiveSpaces + 1), StringComparison.Ordinal);
}
