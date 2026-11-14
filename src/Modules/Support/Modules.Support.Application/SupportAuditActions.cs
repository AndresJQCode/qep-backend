namespace Modules.Support.Application;

public static class SupportAuditActions
{
    public const string TicketCreated = "support.ticket.created";

    public const string TicketStatusChanged = "support.ticket.status_changed";

    public const string TicketAssigned = "support.ticket.assigned";

    public const string TicketTypeChanged = "support.ticket.type_changed";

    public const string TicketCustomerChanged = "support.ticket.customer_changed";

    public const string TicketTypeCreated = "support.ticket_type.created";

    public const string TicketTypeUpdated = "support.ticket_type.updated";

    public const string TicketTypesReordered = "support.ticket_type.reordered";

    public static class Resources
    {
        public const string Ticket = "ticket";

        public const string TicketType = "ticket_type";
    }
}
