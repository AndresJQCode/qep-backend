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
    long Version,
    ConversationChannel Channel = ConversationChannel.WhatsApp,
    Guid? ResolutionReasonId = null,
    DateTimeOffset LastActivityAt = default,
    ConversationAutomationStatus AutomationStatus = ConversationAutomationStatus.None,
    DateTimeOffset? AutomationStartedAt = null,
    DateTimeOffset? AutomationPausedAt = null,
    Guid? AutomationPausedByMemberId = null,
    AutomationPauseReason? AutomationPauseReason = null,
    Guid? AutomationChatbotId = null,
    string? AutomationStep = null,
    DateTimeOffset? AutomationResumedAt = null,
    DateTimeOffset? BlockedAt = null,
    Guid? BlockedByMemberId = null);

/// <summary>Spec 2026-10-10 §5.1: <c>assigned=all|me|none</c>.</summary>
public enum AssignedFilter
{
    All,
    Mine,
    Unassigned,
}

/// <summary>§6.1.6. <c>MemberId</c> = la membresía de quien llama (para <see cref="AssignedFilter.Mine"/>); <c>CustomerIds</c>
/// salen del nombre del cliente; <c>CustomerWaIds</c>, para las conversaciones viejas sin <c>customer_id</c>.</summary>
public sealed record ConversationListFilter(
    ConversationStatus Status,
    AssignedFilter Assigned,
    Guid? MemberId,
    string? Search,
    IReadOnlyCollection<string> CustomerWaIds,
    IReadOnlyCollection<Guid> CustomerIds,
    ConversationChannel? Channel = null,
    IReadOnlyCollection<Guid>? TagCustomerIds = null,
    bool UnreadOnly = false,
    ConversationSort Sort = ConversationSort.Newest,
    AutomationFilter? Automation = null);

/// <summary>Spec 2026-10-21 §4.5: <c>automation=any|active|paused|none</c>; <c>null</c> = sin filtro, <c>Any</c> = cualquiera distinta de <c>None</c>.</summary>
public enum AutomationFilter
{
    Any,
    Active,
    Paused,
    None,
}

/// <summary>Spec qep-frontend 2026-10-15: más recientes primero (el de siempre) o más antiguas primero, por <c>last_activity_at</c>.</summary>
public enum ConversationSort
{
    Newest,
    Oldest,
}

/// <summary>Spec 2026-10-09 §7.6: las lecturas de la bandeja. Todo método recibe <c>tenantId</c>.</summary>
public interface IConversationQueries
{
    /// <summary>«Lista sin/con búsqueda» de §7.6: <c>WHERE tenant_id = @t AND status = @s [AND assigned_member_id = @m |
    /// IS NULL] [AND (profile_name ILIKE @p OR username ILIKE @p OR wa_id LIKE @d OR customer_id = ANY(@customerIds) OR
    /// (customer_id IS NULL AND wa_id = ANY(@phones)))] [AND (last_activity_at, id) &lt; (@t, @id)] ORDER BY last_activity_at DESC,
    /// id DESC LIMIT @take</c> (al revés con <see cref="ConversationSort.Oldest"/>), por <c>IX_conversations_tenant_status_activity</c>
    /// o los parciales de asignado (spec 2026-10-10 §6.1.6). Sin <c>count(*)</c>: la bandeja pagina por cursor.</summary>
    Task<IReadOnlyList<ConversationRow>> ListAsync(
        Guid tenantId, ConversationListFilter filter, ConversationCursor? after, int take, CancellationToken cancellationToken);

    /// <summary>«Contadores» de §7.6: <c>count(*) WHERE status = 'Open'</c>, <c>SUM(unread_count) WHERE
    /// unread_count &gt; 0</c>, y las abiertas mías y sin asignar (D-A10), por los índices parciales. Sin búsqueda.
    /// Sin <paramref name="memberId"/> (quien llama no tiene membresía activa), <c>mine</c> es 0.</summary>
    Task<ConversationCountsDto> CountsAsync(Guid tenantId, Guid? memberId, CancellationToken cancellationToken);

    /// <summary>Una conversación del tenant; <c>null</c> si no existe o es de otro tenant.</summary>
    Task<ConversationRow?> FindAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken);

    /// <summary>Las conversaciones de una página de resultados de búsqueda (§8.8), en una consulta.</summary>
    Task<IReadOnlyList<ConversationRow>> FindManyAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);
}
