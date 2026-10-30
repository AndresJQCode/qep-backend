namespace Modules.Messaging.Domain;

/// <summary>Los códigos del spec 2026-10-09 §10.1, en un solo lugar.</summary>
public static class MessagingErrorCodes
{
    public const string WindowClosed = "messaging.window_closed";
    public const string ConnectionUnavailable = "messaging.connection_unavailable";
    public const string ConversationNotOpen = "messaging.conversation.not_open";
    public const string MessageRejected = "messaging.message.rejected";
    public const string AlreadyResolved = "messaging.conversation.already_resolved";
    public const string AlreadyOpen = "messaging.conversation.already_open";
    public const string ConversationNotFound = "messaging.conversation.not_found";
    public const string MessageNotFound = "messaging.message.not_found";
    public const string WebhookSignatureInvalid = "messaging.webhook.signature_invalid";

    /// <summary>Spec 2026-10-14 §5.2: plantillas, campañas e inicio de conversación no existen en Instagram (§3).</summary>
    public const string ChannelUnsupported = "messaging.channel_unsupported";

    /// <summary>Spec 2026-10-10 §10: enviar a una conversación asignada a otra membresía.</summary>
    public const string AssignedToOther = "messaging.conversation.assigned_to_other";

    /// <summary>Spec 2026-10-10 §10: transferir a una membresía que no es activa del tenant o no tiene manage.</summary>
    public const string AssigneeCannotReply = "messaging.conversation.assignee_cannot_reply";

    /// <summary>Spec 2026-10-24 §4.2: el cliente elegido no existe en el tenant (422). Otro tenant responde igual que uno inexistente.</summary>
    public const string ConversationCustomerNotFound = "messaging.conversation.customer_not_found";

    /// <summary>Spec 2026-10-11 §4.1: R2 no aceptó la nota de voz. No queda fila; reintentar es seguro.</summary>
    public const string MediaUploadFailed = "messaging.media.upload_failed";

    /// <summary>Messaging-media-reply R15: el <c>uploadId</c> no resuelve a ningún objeto en la clave del miembro (ajeno,
    /// vencido, ya consumido o nunca subido). Un id de otro tenant responde igual. 422.</summary>
    public const string MediaUploadNotFound = "messaging.media.upload_not_found";

    /// <summary>R10: el archivo pasa del tope de su tipo (25 MB una imagen, 100 MB un documento), declarado o medido en el bucket.</summary>
    public const string MediaTooLarge = "messaging.media.too_large";

    /// <summary>R9: el tipo no está permitido, la extensión no casa con el mime o la firma del contenido no casa con la extensión.</summary>
    public const string MediaUnsupportedFormat = "messaging.media.unsupported_format";

    /// <summary>R12: la firma dice jpeg o png pero la imagen no decodifica (o pasa de 40 megapíxeles).</summary>
    public const string MediaInvalidImage = "messaging.media.invalid_image";

    /// <summary>Spec 2026-10-11 §5.2: la plantilla no está en el catálogo de la conexión, no está aprobada o v1 no sabe enviarla.</summary>
    public const string TemplateNotAvailable = "messaging.template.not_available";

    /// <summary>§5.2: las claves de <c>parameters</c> no son exactamente las de la plantilla.</summary>
    public const string TemplateParametersMismatch = "messaging.template.parameters_mismatch";

    /// <summary>§5.2: <c>POST /conversations</c> con un cliente que no existe en el tenant o está inactivo (404).</summary>
    public const string CustomerNotFound = "messaging.customer.not_found";

    /// <summary>§5.2: el cliente no tiene teléfono ni BSUID: no hay a dónde escribirle.</summary>
    public const string CustomerUnreachable = "messaging.customer.unreachable";

    /// <summary>Agregado al implementar E2 (el spec no lo nombra): Meta no contestó la lista de plantillas, así que no hay catálogo
    /// contra el cual validar ni nada que mostrar. Es un 422 como <see cref="ConnectionUnavailable"/>: reintentar es seguro.</summary>
    public const string TemplatesUnavailable = "messaging.templates.unavailable";

    /// <summary>Spec 2026-10-11 §6.2: la campaña no existe en el tenant (404).</summary>
    public const string CampaignNotFound = "messaging.campaign.not_found";

    /// <summary>§6.2: sólo un borrador se edita o se borra (409).</summary>
    public const string CampaignNotEditable = "messaging.campaign.not_editable";

    /// <summary>§6.2: sólo una campaña programada o enviándose se cancela (409).</summary>
    public const string CampaignNotCancellable = "messaging.campaign.not_cancellable";

    /// <summary>Sólo una campaña completa o detenida reencola sus fallidos (409).</summary>
    public const string CampaignNotRetriable = "messaging.campaign.not_retriable";

    /// <summary>La campaña no tiene fallidos que se puedan reintentar: los que fallaron por opt-out no cuentan (409).</summary>
    public const string CampaignNothingToRetry = "messaging.campaign.nothing_to_retry";

    /// <summary>Agregado al implementar E3 (el spec no lo nombra): el dominio de <c>Campaign</c> es la última línea de defensa de las
    /// reglas de entrada que el validador ya marca por campo; llegar acá sería un hueco del validador. Es un 422 como los demás.</summary>
    public const string CampaignInvalid = "messaging.campaign.invalid";

    /// <summary>Spec 2026-10-12 §3.2: el atajo no cumple <c>^[a-z0-9-]{2,32}$</c> después de recortarlo y pasarlo a minúsculas.</summary>
    public const string QuickReplyInvalidShortcut = "messaging.quick_reply.invalid_shortcut";

    /// <summary>§3.2: el cuerpo, recortado, está vacío o pasa de 4096 caracteres.</summary>
    public const string QuickReplyInvalidBody = "messaging.quick_reply.invalid_body";

    /// <summary>§3.2 y D-Q3: un <c>{…}</c> que no es <c>{nombre}</c>, <c>{asesor}</c> ni <c>{empresa}</c>.</summary>
    public const string QuickReplyUnknownVariable = "messaging.quick_reply.unknown_variable";

    /// <summary>§3.4: otra respuesta del tenant ya usa ese atajo (<c>IX_quick_replies_tenant_shortcut</c>). 422, como
    /// <c>catalog.product.code_taken</c>.</summary>
    public const string QuickReplyShortcutTaken = "messaging.quick_reply.shortcut_taken";

    /// <summary>§3.3: la respuesta no existe en el tenant (404). Un id de otro tenant responde igual.</summary>
    public const string QuickReplyNotFound = "messaging.quick_reply.not_found";

    /// <summary>Spec 2026-10-15 §3.10: el motivo de resolución no está vigente en el tenant (archivado, de otro tenant o inexistente).</summary>
    public const string ResolutionReasonNotFound = "messaging.resolution_reason.not_found";

    /// <summary>Spec 2026-10-15 §3.10 y D-R11: otro motivo vigente del tenant ya usa ese nombre (409).</summary>
    public const string ResolutionReasonNameTaken = "messaging.resolution_reason.name_taken";

    public const string ResolutionReasonNameRequired = "messaging.resolution_reason.name_required";

    public const string ResolutionReasonNameTooLong = "messaging.resolution_reason.name_too_long";

    /// <summary>Spec 2026-10-15 D-R9: el motivo 101 vigente del tenant.</summary>
    public const string ResolutionReasonLimitReached = "messaging.resolution_reason.limit_reached";

    /// <summary>Spec 2026-10-14 §4.8: el nombre no cumple <c>^[a-z0-9_]{1,512}$</c>. Última línea de defensa del dominio; el validador ya
    /// lo marca en <c>name</c>.</summary>
    public const string TemplateNameInvalid = "messaging.template.name_invalid";

    /// <summary>§4.5: <c>qep_cotizacion</c> y <c>qep_cotizacion_*</c> son de la plantilla que aprovisiona QEP.</summary>
    public const string TemplateNameReserved = "messaging.template.name_reserved";

    /// <summary>§4.8: otra plantilla de la conexión ya se llama así en ese idioma (en el espejo o en Meta). Sin mapa <c>errors</c>, como
    /// <c>quick_reply.shortcut_taken</c>.</summary>
    public const string TemplateNameTaken = "messaging.template.name_taken";

    /// <summary>§4.8: el WABA llegó al tope de plantillas que Meta le permite.</summary>
    public const string TemplateLimitReached = "messaging.template.limit_reached";

    /// <summary>§4.8: Meta no aceptó el contenido; el mensaje lleva el <c>error_user_msg</c> de Graph.</summary>
    public const string TemplateRejectedByMeta = "messaging.template.rejected_by_meta";

    /// <summary>§4.5: la Resumable Upload del PDF de ejemplo falló. No queda fila; reintentar es seguro.</summary>
    public const string TemplateUploadFailed = "messaging.template.upload_failed";

    /// <summary>§4.5: la conexión ya tiene su plantilla de cotización en <c>PENDING</c>, <c>APPROVED</c> o <c>IN_APPEAL</c>.</summary>
    public const string TemplateQuotationExists = "messaging.template.quotation_exists";

    /// <summary>§4.5: existe una <c>qep_cotizacion</c> creada fuera de QEP cuyo cuerpo no es el de la definición.</summary>
    public const string TemplateQuotationConflict = "messaging.template.quotation_conflict";

    /// <summary>§4.3: una campaña <c>Scheduled</c> o <c>Sending</c> de la conexión referencia nombre e idioma.</summary>
    public const string TemplateInUseByCampaign = "messaging.template.in_use_by_campaign";

    /// <summary>§4.8: el <c>templateId</c> no es de esa conexión (404; uuid local, no filtra nada).</summary>
    public const string TemplateNotFound = "messaging.template.not_found";

    /// <summary>Spec 2026-10-21 §4.2: la conversación no admite esa operación del asistente (sin asistente, con asesor o ya pausado).</summary>
    public const string AutomationNotAvailable = "messaging.automation.not_available";

    /// <summary>Spec 2026-10-21 §4.2: el asistente ya está pausado.</summary>
    public const string AutomationAlreadyPaused = "messaging.automation.already_paused";

    /// <summary>Spec 2026-10-21 §4.2: el asistente ya está atendiendo la conversación.</summary>
    public const string AutomationAlreadyActive = "messaging.automation.already_active";

    /// <summary>Spec 2026-10-28 D-9: el rango del reporte supera el tope de su agrupación (92 días, 53 semanas o 24 meses).</summary>
    public const string ReportRangeTooLarge = "messaging.report.range_too_large";

    /// <summary>Spec 2026-10-21 §5: el horario no existe en el tenant (404).</summary>
    public const string BusinessHoursNotFound = "messaging.business_hours.not_found";

    /// <summary>Spec 2026-10-21 §5: el nombre va de 1 a 60 caracteres.</summary>
    public const string BusinessHoursNameInvalid = "messaging.business_hours.name_invalid";

    /// <summary>Spec 2026-10-21 §5: franjas fuera de 0..1440, solapadas, desordenadas, más de tres por día o semana incompleta.</summary>
    public const string BusinessHoursRangesInvalid = "messaging.business_hours.ranges_invalid";

    /// <summary>Spec 2026-10-21 §5: fechas de excepción repetidas, más de 100 vigentes o franjas inválidas.</summary>
    public const string BusinessHoursExceptionsInvalid = "messaging.business_hours.exceptions_invalid";

    /// <summary>Spec 2026-10-21 §5: el país de festivos no es null ni CO.</summary>
    public const string BusinessHoursHolidayCountryInvalid = "messaging.business_hours.holiday_country_invalid";

    /// <summary>Spec 2026-10-21 §5: ya hay un horario con ese nombre en el tenant.</summary>
    public const string BusinessHoursNameTaken = "messaging.business_hours.name_taken";

    /// <summary>Spec 2026-10-21 §5: ya hay otro horario predeterminado en el tenant.</summary>
    public const string BusinessHoursDefaultConflict = "messaging.business_hours.default_conflict";

    /// <summary>Spec 2026-10-21 §5: el horario tiene conexiones asignadas.</summary>
    public const string BusinessHoursInUse = "messaging.business_hours.in_use";

    /// <summary>Spec 2026-10-21 §5: el horario predeterminado no se borra.</summary>
    public const string BusinessHoursIsDefault = "messaging.business_hours.is_default";

    /// <summary>Spec 2026-10-26 §3.3: bloquear sólo existe en WhatsApp con número (no Instagram ni conversaciones sin <c>wa_id</c>).</summary>
    public const string BlockUnsupportedChannel = "messaging.contact.block_unsupported_channel";

    /// <summary>Spec 2026-10-26 §3.3: Meta sólo deja bloquear a quien escribió en las últimas 24 horas.</summary>
    public const string BlockWindowClosed = "messaging.contact.block_window_closed";

    /// <summary>Spec 2026-10-26: Meta no aceptó el bloqueo.</summary>
    public const string BlockFailed = "messaging.contact.block_failed";

    /// <summary>Spec 2026-10-26: Meta no aceptó el desbloqueo.</summary>
    public const string UnblockRejected = "messaging.contact.unblock_rejected";

    /// <summary>Spec 2026-10-26: el contacto está bloqueado, así que la conversación no envía.</summary>
    public const string ContactBlocked = "messaging.contact.blocked";

    /// <summary>Spec 2026-10-29: la importación del historial no se puede reintentar (rechazada, vencida o sin nada que pedir).</summary>
    public const string HistorySyncRetryNotAllowed = "messaging.history_sync.retry_not_allowed";

    /// <summary>Spec 2026-10-29: la conexión no tiene importación del historial.</summary>
    public const string HistorySyncNotFound = "messaging.history_sync.not_found";
}
