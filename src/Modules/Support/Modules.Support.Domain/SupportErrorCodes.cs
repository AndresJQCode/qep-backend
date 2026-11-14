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
}
