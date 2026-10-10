using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>Una fila de <c>messaging.messages</c> (con su medio, si tiene) para leer.
/// <c>failure_title</c> no está: nunca sale por HTTP (§10.3).</summary>
public sealed record MessageRow(
    Guid Id,
    Guid ConversationId,
    MessageDirection Direction,
    MessageKind Kind,
    string? Text,
    string? Caption,
    string? DetailsJson,
    MessageStatus Status,
    int? FailureCode,
    DateTimeOffset OccurredAt,
    Guid? SentByMemberId,
    Guid? ClientId,
    MessageMediaRow? Media,
    Guid? ReplyToMessageId = null);

public sealed record MessageMediaRow(string MimeType, string? FileName);

/// <summary>Spec 2026-10-10 §8.6: lo justo del mensaje citado para la burbuja y para validar un <c>replyTo</c> saliente.</summary>
public sealed record ReplyTargetRow(Guid Id, Guid ConversationId, MessageDirection Direction, MessageKind Kind, string? Text, string? Caption, string? Wamid);

/// <summary>La posición de un mensaje en el orden <c>(occurred_at, id)</c> del hilo.</summary>
public sealed record MessageCursor(DateTimeOffset OccurredAt, Guid Id);

/// <summary>Spec 2026-10-09 §7.6: el hilo de una conversación.</summary>
public interface IMessageQueries
{
    /// <summary>El cursor de <paramref name="messageId"/> sólo si es de <paramref name="conversationId"/>;
    /// <c>null</c> si no (Review Focus 3).</summary>
    Task<MessageCursor?> FindCursorAsync(Guid conversationId, Guid messageId, CancellationToken cancellationToken);

    /// <summary>«Hilo» e «Hilo hacia atrás» de §7.6: <c>WHERE conversation_id = @c [AND (occurred_at, id) &lt;
    /// (@t, @id)] ORDER BY occurred_at DESC, id DESC LIMIT @take</c>, por <c>IX_messages_thread</c>. Del más
    /// nuevo al más viejo.</summary>
    Task<IReadOnlyList<MessageRow>> ListThreadAsync(Guid conversationId, MessageCursor? before, int take, CancellationToken cancellationToken);

    /// <summary>§8.6 (P10): los citados de una página en una consulta, por tenant y PK. Los que no están no aparecen.</summary>
    Task<IReadOnlyDictionary<Guid, ReplyTargetRow>> FindReplyTargetsAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);
}
