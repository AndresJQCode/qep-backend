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
}
