namespace Modules.Support.Application;

public static class SupportAuditActions
{
    public const string TicketCreated = "support.ticket.created";

    public const string TicketStatusChanged = "support.ticket.status_changed";

    public const string TicketAssigned = "support.ticket.assigned";

    public const string TicketTypeChanged = "support.ticket.type_changed";

    public const string TicketCustomerChanged = "support.ticket.customer_changed";

    public const string TicketPriorityChanged = "support.ticket.priority_changed";

    /// <summary>Spec 2026-11-22: cambió la fecha límite de una tarea interna (o se la quitaron). Lleva sólo el nombre de campo
    /// <c>dueOn</c>.</summary>
    public const string TicketDueDateChanged = "support.ticket.due_date_changed";

    /// <summary>Fase 4a (plan G11): la prórroga. Lleva los nombres de campo <c>dueOn</c> y <c>extension</c>, nunca el motivo.</summary>
    public const string TicketExtended = "support.ticket.extended";

    /// <summary>Spec 2026-11-16 (P-4): un asesor generó un PIN nuevo para un anónimo. Lleva sólo el nombre de campo
    /// <c>pinIssuedAt</c>, nunca el PIN ni su hash.</summary>
    public const string TicketPinReissued = "support.ticket.pin_reissued";

    public const string TicketTypeCreated = "support.ticket_type.created";

    public const string TicketTypeUpdated = "support.ticket_type.updated";

    public const string TicketTypesReordered = "support.ticket_type.reordered";

    public const string TicketTypeDeleted = "support.ticket_type.deleted";

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

    /// <summary>Spec 2026-11-15 §12 (plan L21): a version of a legal document was published. The resource is the version; the
    /// changed fields are names only, never the content.</summary>
    public const string LegalDocumentPublished = "support.legal_document.published";

    /// <summary>Plan F37: alguien de la consola pidió la URL de un adjunto del formulario público. El recurso es el ticket.</summary>
    public const string TicketAttachmentDownloaded = "support.ticket.attachment_downloaded";

    /// <summary>Fase 4b (H13): alguien de la consola pidió la URL del PDF de una respuesta formal. El recurso es el ticket.</summary>
    public const string TicketReplyDocumentDownloaded = "support.ticket.reply_document_downloaded";

    /// <summary>Fase 4b (H16): el worker mandó la encuesta (o la publicó en la página de consulta). Acción del sistema; el recurso es el
    /// ticket y el campo, <c>survey</c>.</summary>
    public const string TicketSurveySent = "support.ticket.survey_sent";

    /// <summary>Fase 4b (H16): «sin encuesta» (apagada, sin plantilla, sin autor, contacto bloqueado…). Acción del sistema.</summary>
    public const string TicketSurveySkipped = "support.ticket.survey_skipped";

    /// <summary>Fase 4b (H16): la encuesta falló en todos sus intentos. Acción del sistema.</summary>
    public const string TicketSurveyFailed = "support.ticket.survey_failed";

    /// <summary>Fase 4b (H18 a H21): el solicitante calificó la encuesta (por WhatsApp, por el enlace o por la página de consulta). Acción del
    /// sistema; el campo es <c>survey</c>, nunca la calificación ni el comentario.</summary>
    public const string TicketSurveyAnswered = "support.ticket.survey_answered";

    /// <summary>Fase 4b (H18, Q-11): se anotó el mensaje que pide el comentario después del toque, salido o con su motivo. Acción del
    /// sistema.</summary>
    public const string TicketSurveyPrompted = "support.ticket.survey_prompted";

    /// <summary>Fase 4b (H18): el texto del contacto después del toque quedó como comentario de la encuesta. Acción del sistema; nunca el
    /// texto.</summary>
    public const string TicketSurveyCommented = "support.ticket.survey_commented";

    /// <summary>Spec 2026-11-15 §5.4 (plan N21): an advisor added an internal note. The resource is the ticket; the text is not
    /// recorded.</summary>
    public const string TicketNoteAdded = "support.ticket.note_added";

    public static class Resources
    {
        /// <summary>La configuración del formulario público del tenant; su id es el del tenant, como <see cref="Settings"/>.</summary>
        public const string PublicForm = "public_form";

        /// <summary>A version of a legal document; its id is the version's.</summary>
        public const string LegalDocument = "legal_document";

        public const string Ticket = "ticket";

        /// <summary>Un entrante sin clasificar; su id es el del mensaje de Mensajería.</summary>
        public const string UnclassifiedMessage = "unclassified_message";

        public const string TicketType = "ticket_type";

        public const string Settings = "settings";
    }
}
