namespace Modules.Support.Domain;

/// <summary>
/// Códigos estables de error del módulo (spec 2026-11-14). El mapeo a HTTP vive en <c>ApiExceptionHandler</c>; acá sólo el
/// contrato.
/// </summary>
public static class SupportErrorCodes
{
    public const string TicketNotFound = "support.ticket.not_found";

    public const string TransitionInvalid = "support.ticket.transition_invalid";

    public const string RequesterRequired = "support.ticket.requester_required";

    public const string ReplyChannelUnavailable = "support.ticket.reply_channel_unavailable";

    public const string CustomerNotFound = "support.ticket.customer_not_found";

    public const string CustomerInactive = "support.ticket.customer_inactive";

    public const string AssigneeInvalid = "support.ticket.assignee_invalid";

    public const string TicketTypeNotFound = "support.ticket_type.not_found";

    public const string TicketTypeUnavailable = "support.ticket_type.unavailable";

    public const string TicketTypeNameTaken = "support.ticket_type.name_taken";

    public const string TicketTypeOrderIncomplete = "support.ticket_type.order_incomplete";

    // Fase 2, Mensajería (spec §8.1 a §8.3).
    public const string ConversationNotFound = "support.conversation.not_found";

    public const string MessageNotInConversation = "support.message.not_in_conversation";

    public const string MessageNotLinked = "support.message.not_linked";

    public const string UnclassifiedNotPending = "support.unclassified.not_pending";

    public const string TicketNotCandidate = "support.unclassified.ticket_not_candidate";

    public const string NoConversation = "support.ticket.no_conversation";

    /// <summary>Ruling A-c2: un ticket cerrado no vuelve a seguir el hilo; no podría vincular nada.</summary>
    public const string FollowClosed = "support.ticket.follow_closed";

    public const string ReplyWindowClosed = "support.reply.window_closed";

    public const string ReplyContactBlocked = "support.reply.contact_blocked";

    public const string ReplyTemplateNotApproved = "support.reply.template_not_approved";

    public const string ReplyTemplateTextInvalid = "support.reply.template_text_invalid";

    public const string ReplyConnectionUnavailable = "support.reply.connection_unavailable";

    public const string ReplyConnectionRequired = "support.reply.connection_required";

    public const string ReplyAttachmentUnavailable = "support.reply.attachment_unavailable";

    /// <summary>El nombre del destinatario o el radicado no cumplen las reglas de un parámetro de plantilla de Meta.</summary>
    public const string ReplyRecipientInvalid = "support.reply.recipient_invalid";

    /// <summary>La conversación es de un canal sin plantillas (Instagram).</summary>
    public const string ReplyChannelUnsupported = "support.reply.channel_unsupported";

    // Los tres únicos de la fase 2, traducidos por nombre de índice en SupportUnitOfWork (plan E3 y E16). El seguimiento del hilo y
    // el envío los atrapan: no llegan a la pantalla salvo en una carrera.
    public const string MessageAlreadyLinked = "support.message.already_linked";

    public const string UnclassifiedAlreadyRecorded = "support.unclassified.already_recorded";

    public const string ReplyDuplicateClientId = "support.reply.duplicate_client_id";
}
