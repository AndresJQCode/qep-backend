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

    /// <summary>NIT, CC and CE document numbers only admit digits, dots, dashes and spaces (422). It marks the requester's document number.</summary>
    public const string DocumentNumberNotNumeric = "support.ticket.document_number_not_numeric";

    /// <summary>R9 [owner 2026-11-15]: un ticket resuelto o cerrado no se reasigna, no cambia de tipo ni de cliente; cerrado, tampoco
    /// fija ni suelta mensajes ni cambia «Seguir el hilo» (R9 extendida).</summary>
    public const string NotEditable = "support.ticket.not_editable";

    public const string ReplyChannelUnavailable = "support.ticket.reply_channel_unavailable";

    public const string CustomerNotFound = "support.ticket.customer_not_found";

    public const string CustomerInactive = "support.ticket.customer_inactive";

    public const string AssigneeInvalid = "support.ticket.assignee_invalid";

    /// <summary>Spec 2026-11-22 §8.6: a recipient of the unassigned-ticket alert that cannot manage tickets (or is not a member). Domain
    /// code and not a field error: the id was chosen from a list, so there is no input to mark.</summary>
    public const string AlertRecipientInvalid = "support.settings.alert_recipient_invalid";

    /// <summary>Spec 2026-11-22 §8.6: the schedule of the alert is not one of the tenant's.</summary>
    public const string AlertScheduleInvalid = "support.settings.alert_schedule_invalid";

    /// <summary>Task-notices spec 2026-11-22 §5.4: the WhatsApp connection chosen for the task notices is not one of the tenant connections.</summary>
    public const string TaskNoticeConnectionInvalid = "support.settings.task_notice_connection_invalid";

    public const string TicketTypeNotFound = "support.ticket_type.not_found";

    public const string TicketTypeUnavailable = "support.ticket_type.unavailable";

    public const string TicketTypeNameTaken = "support.ticket_type.name_taken";

    public const string TicketTypeOrderIncomplete = "support.ticket_type.order_incomplete";

    /// <summary>Fase 4a (plan G4/G5): un tipo con algún ticket, en cualquier estado, no se borra; se desactiva.</summary>
    public const string TicketTypeInUse = "support.ticket_type.in_use";

    // Fase 4a (spec §7.2, plan G10): la prórroga es única, antes de que venza y no pasa del término inicial. Las guardas del dominio
    // van en este orden: not_editable, extension_already_used, extension_no_term, extension_clock_stopped, extension_overdue y
    // extension_too_long.

    /// <summary>El ticket ya usó su única prórroga.</summary>
    public const string ExtensionAlreadyUsed = "support.ticket.extension_already_used";

    /// <summary>Un ticket de un tipo sin término no tiene plazo que prorrogar.</summary>
    public const string ExtensionNoTerm = "support.ticket.extension_no_term";

    /// <summary>Con el reloj detenido (esperando al cliente) no se prorroga (owner, P-4).</summary>
    public const string ExtensionClockStopped = "support.ticket.extension_clock_stopped";

    /// <summary>El plazo ya venció: el mismo día del vencimiento todavía se puede, el siguiente ya no.</summary>
    public const string ExtensionOverdue = "support.ticket.extension_overdue";

    /// <summary>La prórroga pasa del término inicial, o sea que el plazo total pasaría del doble (owner, P-3).</summary>
    public const string ExtensionTooLong = "support.ticket.extension_too_long";

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

    // Fase 4b, respuesta formal (H3, H5, H7, H13).

    /// <summary>H5 (owner 2026-11-16, Q-6): ni la versión del formulario del ticket ni la vigente eligieron una empresa, así que el PDF
    /// no tiene membrete (422).</summary>
    public const string ReplyFormalOrganizationRequired = "support.reply.formal_organization_required";

    /// <summary>H7: <c>qcode-pdf</c> no devolvió el PDF, o el bucket privado no lo guardó (revisión final); no se guarda nada (422, como
    /// <c>quotation.pdf.render_failed</c>).</summary>
    public const string ReplyDocumentRenderFailed = "support.reply.document_render_failed";

    /// <summary>H13: la respuesta no existe en el ticket o no tiene documento (404).</summary>
    public const string ReplyDocumentNotFound = "support.reply.document_not_found";

    /// <summary>H3 (owner 2026-11-16, Q-7): firma el responsable del ticket, y el ticket no tiene (422).</summary>
    public const string ReplyFormalSignerRequired = "support.reply.formal_signer_required";

    /// <summary>Revisión del Bloque B: el canal no llegó a enviar la respuesta (el PDF de la respuesta formal no subió a Meta o no se guardó
    /// en R2) y no quedó nada: la respuesta nueva se borra con su PDF (422) y el asesor puede reintentar, también con el mismo
    /// <c>clientId</c>.</summary>
    public const string ReplySendFailed = "support.reply.send_failed";

    // Fase 4b, encuesta de satisfacción (H19 a H21).

    /// <summary>H19: el token del enlace no es de ninguna encuesta del tenant (404, el mismo cuerpo para uno mal formado).</summary>
    public const string SurveyNotFound = "support.survey.not_found";

    /// <summary>H21: la encuesta ya no se responde (sin procesar, apagada, o el ticket se reabrió) (422).</summary>
    public const string SurveyClosed = "support.survey.closed";

    /// <summary>H21: la encuesta ya tiene calificación; la primera gana (422).</summary>
    public const string SurveyAlreadyAnswered = "support.survey.already_answered";

    // Fase 3, formulario público (spec §9, plan F1 a F37). Los de support.public.* los ve la página pública; el resto, la consola.

    /// <summary>Slug desconocido, tenant inactivo, módulo apagado o formulario sin publicar: el mismo 404 (plan F3).</summary>
    public const string PublicNotFound = "support.public.not_found";

    /// <summary>Radicado desconocido, PIN errado o radicado bloqueado: la misma respuesta (plan F9, anti-enumeración).</summary>
    public const string PublicLookupFailed = "support.public.lookup_failed";

    public const string PublicCaptchaInvalid = "support.public.captcha_invalid";

    /// <summary>Alguna versión de los documentos legales que aplican al camino (aviso de privacidad o anónimo, y términos) ya no es la
    /// vigente: la pantalla recarga (spec 2026-11-15 §9, plan L14).</summary>
    public const string PublicLegalTextsOutdated = "support.public.legal_texts_outdated";

    public const string PublicFormOutdated = "support.public.form_outdated";

    /// <summary>El <c>ErrorCode</c> de la regla del validador sobre <c>AcceptsDataPolicy</c>. Por HTTP sale como
    /// <c>422 validation.failed</c> con <c>errors.AcceptsDataPolicy</c>: <c>ApiExceptionHandler</c> no publica el código (plan L15).</summary>
    public const string PublicConsentRequired = "support.public.consent_required";

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

    /// <summary>Spec 2026-11-16 (P-1): sólo un ticket anónimo recibe un PIN nuevo desde la consola; uno con solicitante o cliente usa el
    /// reenvío público, que va a su correo o a su hilo. También lo responde un ticket sin fila de acceso.</summary>
    public const string PinNotAnonymous = "support.ticket.pin_not_anonymous";

    /// <summary>Completar antes de que el navegador termine el PUT: el archivo todavía no está en el bucket (428).</summary>
    public const string PublicAttachmentNotUploaded = "support.public.attachment_not_uploaded";

    /// <summary>El adjunto no es de ese ticket del tenant (consola, plan F37).</summary>
    public const string AttachmentNotFound = "support.attachment.not_found";

    /// <summary>Publicar el formulario exige una versión de cada documento legal (spec 2026-11-15 §7; el bloqueador dice cuál falta).</summary>
    public const string FormLegalTextsRequired = "support.form.legal_texts_required";

    public const string FormNoPublishedTypes = "support.form.no_published_types";

    /// <summary>Spec 2026-11-15 §9.3 (plan L31): a <c>PUT</c> that changes the company to one that does not exist, is of another tenant
    /// or is inactive.</summary>
    public const string FormOrganizationUnavailable = "support.form.organization_unavailable";

    /// <summary>Plan L32, D-8: publishing without a chosen company.</summary>
    public const string FormOrganizationRequired = "support.form.organization_required";

    /// <summary>Plan L32: publishing with the chosen company deactivated.</summary>
    public const string FormOrganizationInactive = "support.form.organization_inactive";

    /// <summary>Traducido por nombre de índice (<c>IX_form_versions_tenant_version</c>): dos guardados de la misma versión.</summary>
    public const string FormVersionTaken = "support.form.version_taken";

    // Spec 2026-11-15 (legal documents of the portal, §10).

    /// <summary>After sanitizing, the document has no visible text (422).</summary>
    public const string LegalDocumentsContentEmpty = "support.legal_documents.content_empty";

    /// <summary>After sanitizing, the document has more than <c>SupportLegalDocumentVersion.ContentMaxLength</c> characters (422).</summary>
    public const string LegalDocumentsContentTooLong = "support.legal_documents.content_too_long";

    /// <summary>The <c>{kind}</c> of the route is not <c>privacy-notice</c>, <c>anonymous-notice</c> or <c>terms</c> (404).</summary>
    public const string LegalDocumentsKindNotFound = "support.legal_documents.kind_not_found";

    public const string LegalDocumentsVersionNotFound = "support.legal_documents.version_not_found";

    /// <summary>Spec 2026-11-16 (D-F8): el HTML saneado o la pregunta de autorización traen un <c>{{…}}</c> que no es una de
    /// <c>SupportLegalVariables.All</c> (422). En singular, <c>legal_document</c>, porque así quedó fijado en el contrato con la consola.</summary>
    public const string LegalDocumentUnknownVariable = "support.legal_document.unknown_variable";

    /// <summary>Translated by index name (<c>IX_legal_document_versions_tenant_kind_version</c>): two publications at once (409).</summary>
    public const string LegalDocumentsVersionTaken = "support.legal_documents.version_taken";

    /// <summary>The ticket has no acceptance of that kind (404).</summary>
    public const string LegalAcceptanceNotFound = "support.legal_acceptance.not_found";

    /// <summary>The <c>ErrorCode</c> of the validator's rule on <c>AcceptsTerms</c>, like <see cref="PublicConsentRequired"/>. Over HTTP
    /// it is <c>422 validation.failed</c> with <c>errors.AcceptsTerms</c>: <c>ApiExceptionHandler</c> does not publish the code.</summary>
    public const string PublicTermsRequired = "support.public.terms_required";

    /// <summary>Translated by index name (<c>IX_legal_acceptances_ticket_kind</c>, 409). Unreachable today: the acceptances are written
    /// only with a ticket just created, in the same transaction.</summary>
    public const string PublicLegalAcceptanceDuplicate = "support.public.legal_acceptance_duplicate";

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

    // Spec 2026-11-22: tareas internas entre miembros.

    /// <summary>The internal ticket type cannot be edited or deleted, and no ticket takes it nor leaves it by a type change.</summary>
    public const string InternalTypeLocked = "support.ticket.internal_type_locked";

    /// <summary>An internal task has no requester to reply to: no customer, conversation, follow-thread nor reply.</summary>
    public const string InternalNoReply = "support.ticket.internal_no_reply";

    /// <summary>The due date of a task is today (tenant zone) or later.</summary>
    public const string InternalDueInPast = "support.ticket.internal_due_in_past";

    /// <summary>[owner] D11: the free due date only exists on an internal task.</summary>
    public const string NotInternal = "support.ticket.not_internal";
}
