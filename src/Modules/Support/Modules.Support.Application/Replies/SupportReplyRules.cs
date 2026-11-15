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
/// mismo que el envío hace.</summary>
internal static class SupportReplyRules
{
    public const string NoConversation = "no_conversation";
    public const string MessagingDisabled = "messaging_disabled";
    public const string WindowClosed = "window_closed";
    public const string ContactBlocked = "contact_blocked";
    public const string TemplateNotApproved = "template_not_approved";
    public const string NoMobile = "no_mobile";
    public const string NoEmail = "no_email";

    /// <summary>Las reglas de Meta para el valor de un parámetro de plantilla, copia de <c>TemplateParameterRules.IsValid</c> de
    /// Mensajería (Soporte no la ve): de 1 a 1 024, sin saltos de línea ni tabulaciones ni más de cuatro espacios seguidos. Si cambia allá,
    /// cambia acá.</summary>
    public const int TemplateValueMaxLength = 1024;

    public const int MaxConsecutiveSpaces = 4;

    /// <summary>Instagram no acepta textos de más de 1 000 caracteres (<c>SendMessageValidator.InstagramTextMaxLength</c>).</summary>
    public const int InstagramTextMaxLength = 1000;

    public static bool FitsTemplate(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= TemplateValueMaxLength
        && !value.Contains('\n', StringComparison.Ordinal)
        && !value.Contains('\r', StringComparison.Ordinal)
        && !value.Contains('\t', StringComparison.Ordinal)
        && !value.Contains(new string(' ', MaxConsecutiveSpaces + 1), StringComparison.Ordinal);

    /// <summary>
    /// Plan E13: dentro de la ventana de 24 h, texto; fuera, en WhatsApp la plantilla «Aviso de respuesta» a la misma conversación si su
    /// conexión la tiene aprobada, y en Instagram nada (no tiene plantillas). Un contacto bloqueado no recibe nada. Quien llama ya comprobó
    /// el módulo de Mensajería.
    /// </summary>
    public static async Task<SupportThreadAvailability> ThreadAsync(
        ISupportReplyChannels channels, SupportTicket ticket, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (ticket.ConversationId is not { } conversationId)
        {
            return new SupportThreadAvailability(null, NoConversation, SupportErrorCodes.NoConversation, null);
        }

        var thread = await channels.FindThreadAsync(ticket.TenantId, conversationId, cancellationToken);
        if (thread is null)
        {
            return new SupportThreadAvailability(null, NoConversation, SupportErrorCodes.ConversationNotFound, null);
        }

        if (thread.Blocked)
        {
            return new SupportThreadAvailability(null, ContactBlocked, SupportErrorCodes.ReplyContactBlocked, thread.WindowClosesAt);
        }

        if (thread.WindowClosesAt is { } closesAt && closesAt > now)
        {
            return new SupportThreadAvailability(
                new SupportReplyRoute(SupportReplyDelivery.Text, conversationId, null, null), null, null, closesAt);
        }

        if (thread.Channel == SupportConversationRef.Instagram)
        {
            return new SupportThreadAvailability(null, WindowClosed, SupportErrorCodes.ReplyWindowClosed, thread.WindowClosesAt);
        }

        return await channels.IsTemplateApprovedAsync(ticket.TenantId, thread.ConnectionId, cancellationToken)
            ? new SupportThreadAvailability(
                new SupportReplyRoute(SupportReplyDelivery.Template, conversationId, null, null), null, null, thread.WindowClosesAt)
            : new SupportThreadAvailability(
                null, TemplateNotApproved, SupportErrorCodes.ReplyTemplateNotApproved, thread.WindowClosesAt);
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
        _ => SupportErrorCodes.ReplyConnectionUnavailable,
    };
}
