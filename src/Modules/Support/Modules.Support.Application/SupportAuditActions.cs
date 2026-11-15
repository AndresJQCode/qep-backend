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

    public const string TicketReplySent = "support.ticket.reply_sent";

    public const string TicketReplyFailed = "support.ticket.reply_failed";

    // Fase 3 (formulario público): cada guardado crea una versión nueva.
    public const string FormUpdated = "support.form.updated";

    public const string LegalTextsPublished = "support.legal_texts.published";

    /// <summary>Plan F37: alguien de la consola pidió la URL de un adjunto del formulario público. El recurso es el ticket.</summary>
    public const string TicketAttachmentDownloaded = "support.ticket.attachment_downloaded";

    public static class Resources
    {
        /// <summary>La configuración del formulario público del tenant; su id es el del tenant, como <see cref="Settings"/>.</summary>
        public const string PublicForm = "public_form";

        /// <summary>Los textos legales del tenant; su id es el del tenant.</summary>
        public const string LegalTexts = "legal_texts";

        public const string Ticket = "ticket";

        /// <summary>Un entrante sin clasificar; su id es el del mensaje de Mensajería.</summary>
        public const string UnclassifiedMessage = "unclassified_message";

        public const string TicketType = "ticket_type";

        public const string Settings = "settings";
    }
}
