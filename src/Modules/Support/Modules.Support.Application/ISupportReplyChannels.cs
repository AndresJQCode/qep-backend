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

/// <summary>Una respuesta como texto en el hilo. <c>ClientId</c> es el id de la fila de respuesta de Soporte: un reintento con el mismo
/// no manda dos veces.</summary>
public sealed record SupportChannelThreadSend(
    Guid TenantId, Guid ConversationId, Guid ClientId, Guid AuthorMemberId, string Text, Guid? UploadId);

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

/// <summary>Los motivos de <see cref="SupportChannelSendResult.Rejected"/>: los de Mensajería, copiados por nombre (son contrato con
/// <c>SupportReplyReasons</c>), más <see cref="CustomerNotFound"/>, que pone el adaptador cuando abrir la conversación choca con un
/// cliente borrado.</summary>
public static class SupportChannelReasons
{
    public const string WindowClosed = "window_closed";

    public const string ContactBlocked = "contact_blocked";

    public const string TemplateNotApproved = "template_not_approved";

    public const string TemplateTextInvalid = "template_text_invalid";

    public const string RecipientInvalid = "recipient_invalid";

    public const string TextInvalid = "text_invalid";

    public const string ConnectionUnavailable = "connection_unavailable";

    public const string ConversationNotFound = "conversation_not_found";

    public const string AttachmentUnavailable = "attachment_unavailable";

    public const string ChannelUnsupported = "channel_unsupported";

    public const string CustomerNotFound = "customer_not_found";

    public const string SendFailed = "send_failed";
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
