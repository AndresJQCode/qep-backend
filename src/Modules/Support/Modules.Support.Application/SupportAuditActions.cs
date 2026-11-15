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

    public const string SettingsUpdated = "support.settings.updated";

    // Fase 2 (Mensajería).
    public const string TicketMessagePinned = "support.ticket.message_pinned";

    public const string TicketMessageReleased = "support.ticket.message_released";

    public const string TicketFollowChanged = "support.ticket.follow_changed";

    public const string UnclassifiedAssigned = "support.unclassified.assigned";

    public const string UnclassifiedDismissed = "support.unclassified.dismissed";

    public static class Resources
    {
        public const string Ticket = "ticket";

        /// <summary>Un entrante sin clasificar; su id es el del mensaje de Mensajería.</summary>
        public const string UnclassifiedMessage = "unclassified_message";

        public const string TicketType = "ticket_type";

        public const string Settings = "settings";
    }
}
