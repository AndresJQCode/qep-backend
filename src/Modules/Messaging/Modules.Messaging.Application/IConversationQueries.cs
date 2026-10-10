using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>Una fila de <c>messaging.conversations</c> para leer, sin cargar el agregado.</summary>
public sealed record ConversationRow(
    Guid Id,
    Guid TenantId,
    Guid ConnectionId,
    string? UserId,
    string? WaId,
    string? Username,
    string? ProfileName,
    Guid? CustomerId,
    Guid? AssignedMemberId,
    ConversationStatus Status,
    int UnreadCount,
    DateTimeOffset? LastInboundAt,
    Guid? LastMessageId,
    MessageDirection? LastMessageDirection,
    MessageKind? LastMessageKind,
    string? LastMessagePreview,
    MessageStatus? LastMessageStatus,
    DateTimeOffset? LastMessageAt,
    DateTimeOffset UpdatedAt,
    long Version);

/// <summary>Spec 2026-10-09 §7.6: las lecturas de la bandeja. Todo método recibe <c>tenantId</c>.</summary>
public interface IConversationQueries
{
    /// <summary>«Lista sin/con búsqueda» de §7.6: <c>WHERE tenant_id = @t AND status = @s [AND (profile_name
    /// ILIKE @p OR wa_id LIKE @d OR wa_id = ANY(@phones))] ORDER BY last_activity_at DESC, id DESC LIMIT/OFFSET</c>
    /// más el <c>count(*)</c> exacto, por <c>IX_conversations_tenant_status_activity</c>.</summary>
    Task<(IReadOnlyList<ConversationRow> Items, int Total)> ListAsync(
        Guid tenantId, ConversationStatus status, string? search, IReadOnlyCollection<string> customerWaIds, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>«Contadores» de §7.6: <c>count(*) WHERE status = 'Open'</c> y <c>SUM(unread_count) WHERE
    /// unread_count &gt; 0</c>, por los dos índices parciales. Sin búsqueda.</summary>
    Task<ConversationCountsDto> CountsAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Una conversación del tenant; <c>null</c> si no existe o es de otro tenant.</summary>
    Task<ConversationRow?> FindAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken);

    /// <summary>Las conversaciones de una página de resultados de búsqueda (§8.8), en una consulta.</summary>
    Task<IReadOnlyList<ConversationRow>> FindManyAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);
}
