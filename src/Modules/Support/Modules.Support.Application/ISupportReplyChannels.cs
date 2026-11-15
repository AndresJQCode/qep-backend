namespace Modules.Support.Application;

/// <summary>El estado de la conversación de un ticket para decidir cómo responderle (plan E13): el canal por nombre, si el contacto
/// está bloqueado, cuándo cierra la ventana de 24 h (<c>null</c> si el contacto nunca escribió), su conexión y si esa conexión está
/// activa: por una pausada, borrada o que pide atención no sale nada (<c>connection_unavailable</c> en <c>reply-options</c>).</summary>
public sealed record SupportThreadStatus(
    Guid ConversationId,
    string Channel,
    bool Blocked,
    DateTimeOffset? WindowClosesAt,
    Guid ConnectionId,
    bool ConnectionOperational = true);

/// <summary>Una conexión de WhatsApp por la que se puede mandar la plantilla «Aviso de respuesta» (plan E14).</summary>
public sealed record SupportConnectionOption(Guid Id, string Name);

/// <summary>Una respuesta en el hilo. <c>ClientId</c> es el id de la fila de respuesta de Soporte: un reintento con el mismo no manda dos
/// veces. Con <c>UploadId</c> sale el adjunto que el asesor subió por la sesión de subida de Mensajería, con <c>FileName</c> (el nombre que
/// vio al elegirlo) y <c>Text</c> como pie (plan E19).</summary>
public sealed record SupportChannelThreadSend(
    Guid TenantId, Guid ConversationId, Guid ClientId, Guid AuthorMemberId, string Text, Guid? UploadId, string? FileName = null);

/// <summary>Una respuesta por la plantilla «Aviso de respuesta»: a <c>ConversationId</c> o, sin conversación, a <c>WaId</c> (sólo
/// dígitos) por la conexión <c>ConnectionId</c>.</summary>
public sealed record SupportChannelTemplateSend(
    Guid TenantId,
    Guid? ConversationId,
    Guid? ConnectionId,
    string? WaId,
    Guid? CustomerId,
    string RecipientName,
    Guid ClientId,
    Guid AuthorMemberId,
    string TicketNumber,
    string Text);

/// <summary>Cómo terminó un envío por un canal de Soporte.</summary>
public abstract record SupportChannelSendResult
{
    /// <summary>Salió. Por Mensajería trae el mensaje y la conversación (la que se abrió, si no había); por correo, los dos nulos.</summary>
    public sealed record Sent(Guid? MessageId, Guid? ConversationId) : SupportChannelSendResult;

    /// <summary>No se intentó: <paramref name="Reason"/> es uno de <see cref="SupportChannelReasons"/>. No deja fila en el hilo.</summary>
    public sealed record Rejected(string Reason) : SupportChannelSendResult;

    /// <summary>Se intentó y falló (Meta o el correo lo rechazaron). Por Mensajería la fila del hilo queda <c>Failed</c>.</summary>
    public sealed record Failed(Guid? MessageId, string Code) : SupportChannelSendResult;
}

/// <summary>
/// Tarea 8a: el único vocabulario de los códigos que la pantalla lee de Soporte: el <c>failureCode</c> de una respuesta
/// (<c>ticket_replies.failure_code</c>), el de la información con el PIN (<c>ticket_access.last_failure_code</c>, en
/// <c>ConsultAccess.FailureCode</c>) y el <c>reason</c> de <c>reply-options</c>. Los motivos de
/// <see cref="SupportChannelSendResult.Rejected"/> que vienen de Mensajería son copias por nombre de <c>SupportReplyReasons</c> (Soporte
/// no ve Mensajería); <c>SupportFailureCodeParityTests</c> los amarra. Un código nuevo va acá, y el frontend lo traduce por esta lista.
/// </summary>
public static class SupportChannelReasons
{
    /// <summary>La ventana de 24 h de la conversación está cerrada. Falla de respuesta, de la información con el PIN y motivo de
    /// <c>reply-options</c>.</summary>
    public const string WindowClosed = "window_closed";

    /// <summary>El contacto de la conversación está bloqueado. Falla de respuesta y motivo de <c>reply-options</c>.</summary>
    public const string ContactBlocked = "contact_blocked";

    /// <summary>La conexión no tiene aprobada la plantilla «Aviso de respuesta». Falla de respuesta y motivo de
    /// <c>reply-options</c>.</summary>
    public const string TemplateNotApproved = "template_not_approved";

    /// <summary>El texto no cumple las reglas de un parámetro de plantilla de Meta. Falla de respuesta.</summary>
    public const string TemplateTextInvalid = "template_text_invalid";

    /// <summary>El nombre del destinatario o el radicado no caben en la plantilla. Falla de respuesta.</summary>
    public const string RecipientInvalid = "recipient_invalid";

    /// <summary>El texto no cumple la regla del canal (por ejemplo, el tope de Instagram). Falla de respuesta y de la información con el
    /// PIN.</summary>
    public const string TextInvalid = "text_invalid";

    /// <summary>La conexión está pausada, borrada o pide atención. Falla de respuesta, de la información con el PIN y motivo de
    /// <c>reply-options</c>.</summary>
    public const string ConnectionUnavailable = "connection_unavailable";

    /// <summary>La conversación ya no existe en el tenant. Falla de respuesta y de la información con el PIN.</summary>
    public const string ConversationNotFound = "conversation_not_found";

    /// <summary>El adjunto no está disponible para esa conversación. Falla de respuesta.</summary>
    public const string AttachmentUnavailable = "attachment_unavailable";

    /// <summary>El canal de la conversación no admite el envío (Instagram no tiene plantillas). Falla de respuesta.</summary>
    public const string ChannelUnsupported = "channel_unsupported";

    /// <summary>Abrir la conversación chocó con un cliente borrado; lo pone el adaptador de Soporte. Falla de respuesta.</summary>
    public const string CustomerNotFound = "customer_not_found";

    /// <summary>El canal intentó y no salió (Meta o el correo lo rechazaron). Falla de respuesta y de la información con el PIN.</summary>
    public const string SendFailed = "send_failed";

    /// <summary>El ticket no tiene conversación. Motivo de <c>reply-options</c>.</summary>
    public const string NoConversation = "no_conversation";

    /// <summary>El módulo de Mensajería está apagado en el tenant. Motivo de <c>reply-options</c> y de la ficha de una conversación.</summary>
    public const string MessagingDisabled = "messaging_disabled";

    /// <summary>La foto del solicitante no tiene celular para la plantilla. Motivo de <c>reply-options</c>.</summary>
    public const string NoMobile = "no_mobile";

    /// <summary>La foto del solicitante no tiene correo. Motivo de <c>reply-options</c>.</summary>
    public const string NoEmail = "no_email";

    /// <summary>Ningún miembro activo puede firmar el envío por Mensajería: ni quien creó el ticket, ni su responsable, ni un gestor
    /// (plan F11). Falla de la información con el PIN.</summary>
    public const string NoAuthor = "no_author";

    /// <summary>Un ticket anónimo sólo se responde por la página de consulta (plan F12). Motivo de <c>reply-options</c>.</summary>
    public const string PortalOnly = "portal_only";

    /// <summary>La página de consulta sólo responde a tickets anónimos: un ticket con solicitante no se responde por ahí (plan F12).
    /// Motivo de <c>reply-options</c>.</summary>
    public const string PortalUnavailable = "portal_unavailable";

    /// <summary>El formulario público del tenant no está publicado: el enlace de consulta respondería 404, así que la información con el
    /// PIN no sale. Falla de la información con el PIN.</summary>
    public const string FormUnpublished = "form_unpublished";

    /// <summary>Todos los códigos de esta lista.</summary>
    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        WindowClosed,
        ContactBlocked,
        TemplateNotApproved,
        TemplateTextInvalid,
        RecipientInvalid,
        TextInvalid,
        ConnectionUnavailable,
        ConversationNotFound,
        AttachmentUnavailable,
        ChannelUnsupported,
        CustomerNotFound,
        SendFailed,
        NoConversation,
        MessagingDisabled,
        NoMobile,
        NoEmail,
        NoAuthor,
        PortalOnly,
        PortalUnavailable,
        FormUnpublished,
    };
}

/// <summary>Las respuestas de Soporte por Mensajería (plan E12 a E14). El adaptador de Bootstrapper traduce
/// <c>ISupportReplyOutbound</c> y la disponibilidad de la plantilla de Mensajería a estos tipos.</summary>
public interface ISupportReplyChannels
{
    /// <summary><c>null</c> si la conversación no existe o es de otro tenant.</summary>
    Task<SupportThreadStatus?> FindThreadAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken);

    /// <summary>Las conexiones de WhatsApp activas del tenant con la plantilla «Aviso de respuesta» aprobada, por nombre.</summary>
    Task<IReadOnlyList<SupportConnectionOption>> ListTemplateConnectionsAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Si la conexión tiene la plantilla «Aviso de respuesta» aprobada.</summary>
    Task<bool> IsTemplateApprovedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);

    Task<SupportChannelSendResult> SendThreadAsync(SupportChannelThreadSend send, CancellationToken cancellationToken);

    Task<SupportChannelSendResult> SendTemplateAsync(SupportChannelTemplateSend send, CancellationToken cancellationToken);
}
