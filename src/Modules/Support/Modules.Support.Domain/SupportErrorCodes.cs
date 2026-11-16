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

    /// <summary>R9 [owner 2026-11-15]: un ticket resuelto o cerrado no se reasigna, no cambia de tipo ni de cliente; cerrado, tampoco
    /// fija ni suelta mensajes ni cambia «Seguir el hilo» (R9 extendida).</summary>
    public const string NotEditable = "support.ticket.not_editable";

    public const string ReplyChannelUnavailable = "support.ticket.reply_channel_unavailable";

    public const string CustomerNotFound = "support.ticket.customer_not_found";

    public const string CustomerInactive = "support.ticket.customer_inactive";

    public const string AssigneeInvalid = "support.ticket.assignee_invalid";

    public const string TicketTypeNotFound = "support.ticket_type.not_found";

    public const string TicketTypeUnavailable = "support.ticket_type.unavailable";

    public const string TicketTypeNameTaken = "support.ticket_type.name_taken";

    public const string TicketTypeOrderIncomplete = "support.ticket_type.order_incomplete";

    /// <summary>Fase 4a (plan G4/G5): un tipo con algún ticket, en cualquier estado, no se borra; se desactiva.</summary>
    public const string TicketTypeInUse = "support.ticket_type.in_use";

    // Fase 2, Mensajería (spec §8.1 a §8.3).
    public const string ConversationNotFound = "support.conversation.not_found";

    public const string MessageNotInConversation = "support.message.not_in_conversation";

    public const string MessageNotLinked = "support.message.not_linked";

    public const string UnclassifiedNotPending = "support.unclassified.not_pending";

    public const string TicketNotCandidate = "support.unclassified.ticket_not_candidate";

    public const string NoConversation = "support.ticket.no_conversation";

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

    /// <summary>El canal rechazó la respuesta por un motivo que Soporte todavía no traduce a uno propio.</summary>
    public const string ReplyUnavailable = "support.reply.unavailable";

    // Los tres únicos de la fase 2, traducidos por nombre de índice en SupportUnitOfWork (plan E3 y E16). El seguimiento del hilo y
    // el envío los atrapan: no llegan a la pantalla salvo en una carrera.
    public const string MessageAlreadyLinked = "support.message.already_linked";

    public const string UnclassifiedAlreadyRecorded = "support.unclassified.already_recorded";

    public const string ReplyDuplicateClientId = "support.reply.duplicate_client_id";

    // Fase 3, formulario público (spec §9, plan F1 a F37). Los de support.public.* los ve la página pública; el resto, la consola.

    /// <summary>Slug desconocido, tenant inactivo, módulo apagado o formulario sin publicar: el mismo 404 (plan F3).</summary>
    public const string PublicNotFound = "support.public.not_found";

    /// <summary>Radicado desconocido, PIN errado o radicado bloqueado: la misma respuesta (plan F9, anti-enumeración).</summary>
    public const string PublicLookupFailed = "support.public.lookup_failed";

    public const string PublicCaptchaInvalid = "support.public.captcha_invalid";

    /// <summary>La versión de los textos legales que aceptó la persona ya no es la vigente: la pantalla recarga (plan F17).</summary>
    public const string PublicLegalTextsOutdated = "support.public.legal_texts_outdated";

    public const string PublicFormOutdated = "support.public.form_outdated";

    public const string PublicConsentRequired = "support.public.consent_required";

    /// <summary>Triage #2 del Bloque F: el ticket ya tiene su consentimiento (<c>IX_consents_ticket</c>, traducido por nombre, 409). Hoy no
    /// se alcanza: el consentimiento se escribe sólo con un ticket recién creado, en la misma transacción.</summary>
    public const string PublicConsentDuplicate = "support.public.consent_duplicate";

    public const string PublicDepartmentNotFound = "support.public.department_not_found";

    public const string PublicBodyTooLarge = "support.public.body_too_large";

    public const string PublicBodyInvalid = "support.public.body_invalid";

    /// <summary>
    /// Plan F32 y F35: un adjunto que no existe, de otro tenant, con otro <c>uploadKey</c>, que no está disponible (sin subir, en
    /// cuarentena) o que ya reclamó otro ticket (<c>IX_ticket_attachments_file</c>, traducido por nombre). La misma respuesta para
    /// todos: no dice cuál de ellos fue.
    /// </summary>
    public const string PublicAttachmentInvalid = "support.public.attachment_invalid";

    /// <summary>Spec §9.3: la versión vigente del formulario no permite adjuntos. Lo responden abrir y completar una subida, y radicar
    /// con una lista de adjuntos no vacía.</summary>
    public const string PublicAttachmentsDisabled = "support.public.attachments_disabled";

    /// <summary>Owner, 2026-11-15 (E2): el tenant ya tiene <c>SupportPublicAttachmentRules.MaxPendingPerTenantPerDay</c> subidas
    /// abiertas en las últimas 24 h sin reclamar por un ticket. Lo responde abrir una subida (422); la página dice «intenta más tarde».</summary>
    public const string PublicAttachmentsBusy = "support.public.attachments_busy";

    /// <summary>Revisión final, I1: un ticket anónimo no guarda cliente (spec §9.4). Lo rechaza el agregado, para todo camino.</summary>
    public const string AnonymousHasNoCustomer = "support.ticket.anonymous_has_no_customer";

    /// <summary>Completar antes de que el navegador termine el PUT: el archivo todavía no está en el bucket (428).</summary>
    public const string PublicAttachmentNotUploaded = "support.public.attachment_not_uploaded";

    /// <summary>El adjunto no es de ese ticket del tenant (consola, plan F37).</summary>
    public const string AttachmentNotFound = "support.attachment.not_found";

    /// <summary>Publicar el formulario exige una versión de los textos legales (plan F15).</summary>
    public const string FormLegalTextsRequired = "support.form.legal_texts_required";

    public const string FormNoPublishedTypes = "support.form.no_published_types";

    /// <summary>Traducido por nombre de índice (<c>IX_form_versions_tenant_version</c>): dos guardados de la misma versión.</summary>
    public const string FormVersionTaken = "support.form.version_taken";

    /// <summary>Traducido por nombre de índice (<c>IX_legal_text_versions_tenant_version</c>).</summary>
    public const string LegalTextsVersionTaken = "support.legal_texts.version_taken";

    /// <summary>Los textos legales necesitan la política como texto o como URL (spec §9.5).</summary>
    public const string LegalTextsPolicyRequired = "support.legal_texts.policy_required";

    /// <summary>Un ticket anónimo sólo se responde por la página de consulta (plan F12).</summary>
    public const string ReplyPortalOnly = "support.reply.portal_only";

    /// <summary>La página de consulta sólo responde a un ticket anónimo (plan F12).</summary>
    public const string ReplyPortalUnavailable = "support.reply.portal_unavailable";

    /// <summary>Spec 2026-11-15 §5.5: the app hub refused a topic (subscribe or presence). A hub error, not HTTP. Owner O3: until a
    /// second module uses the hub, it also covers topics without an authorizer, malformed topics, the per-connection cap and an
    /// unknown presence state.</summary>
    public const string RealtimeTopicForbidden = "support.realtime.topic_forbidden";

    // Spec 2026-11-15 §5.4, internal notes (plan N15 to N21).

    /// <summary>The note's domain guard (<c>SupportTicketNote.Create</c>). Not reachable over HTTP: the validator answers the same text
    /// as <c>validation.failed</c> with <c>errors.text</c> first (plan N16).</summary>
    public const string NoteTextInvalid = "support.note.text_invalid";

    /// <summary>More than five files, or the same file twice.</summary>
    public const string NoteTooManyFiles = "support.note.too_many_files";

    /// <summary>A file that does not exist, belongs to another ticket or member, is not available, or is already in another note
    /// (<c>IX_ticket_note_files_file</c>, translated by name). The same answer for all of them.</summary>
    public const string NoteFileInvalid = "support.note.file_invalid";

    /// <summary>A closed ticket takes no new note nor a new note upload; replaying a note created before closing still answers it.</summary>
    public const string NoteTicketClosed = "support.note.ticket_closed";
}
