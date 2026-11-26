namespace Modules.Support.Application;

/// <summary>Una conversación de Mensajería vista desde Soporte (spec §8.1): el canal por nombre (<c>WhatsApp</c>/<c>Instagram</c>), su
/// cliente, los nombres con que se presenta el contacto, su <c>wa_id</c> (siempre <c>null</c> en Instagram), si está bloqueada y su
/// último entrante.</summary>
public sealed record SupportConversationInfo(
    Guid Id,
    string Channel,
    Guid? CustomerId,
    string? ProfileName,
    string? Username,
    string? WaId,
    bool Blocked,
    DateTimeOffset? LastInboundAt);

/// <summary>Un mensaje del hilo (nunca un evento del sistema). <c>Direction</c> es <c>Inbound</c> u <c>Outbound</c> y <c>Kind</c> el tipo
/// de Mensajería por nombre (<c>Text</c>, <c>Image</c>…). El autor de un saliente es un miembro, un chatbot o una clave de API.</summary>
public sealed record SupportThreadMessage(
    Guid Id,
    Guid ConversationId,
    string Direction,
    string Kind,
    string? Text,
    string? Caption,
    DateTimeOffset OccurredAt,
    Guid? SentByMemberId,
    Guid? SentByChatbotId,
    string? SentByApiKeyName,
    string? MediaMimeType,
    string? MediaFileName);

/// <summary>Lo que Soporte lee de Mensajería sobre una conversación y sus mensajes. El adaptador vive en Bootstrapper, sobre el
/// esquema <c>messaging</c>: Soporte nunca referencia Mensajería. Todo va acotado al tenant.</summary>
public interface ISupportConversationDirectory
{
    /// <summary><c>null</c> si la conversación no existe o es de otro tenant.</summary>
    Task<SupportConversationInfo?> FindAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken);

    /// <summary>Los mensajes de esa conversación con esos ids, sin los eventos del sistema; un id que no está (u otro tenant u otra
    /// conversación) no aparece. Sin orden.</summary>
    Task<IReadOnlyList<SupportThreadMessage>> FindMessagesAsync(
        Guid tenantId, Guid conversationId, IReadOnlyCollection<Guid> messageIds, CancellationToken cancellationToken);
}
