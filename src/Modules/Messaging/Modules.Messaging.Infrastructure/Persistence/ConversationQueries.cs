using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>§7.6: la lista por <c>IX_conversations_tenant_status_activity</c>; los counts por los dos
/// índices parciales; la búsqueda por los GIN de trigramas (ILIKE / LIKE) y por <c>wa_id = ANY</c>.</summary>
internal sealed class ConversationQueries(MessagingDbContext dbContext) : IConversationQueries
{
    private static readonly Expression<Func<Conversation, ConversationRow>> Projection = conversation => new ConversationRow(
        conversation.Id, conversation.TenantId, conversation.ConnectionId, conversation.UserId, conversation.WaId, conversation.Username,
        conversation.ProfileName, conversation.CustomerId, conversation.AssignedMemberId, conversation.Status,
        conversation.UnreadCount, conversation.LastInboundAt, conversation.LastMessageId, conversation.LastMessageDirection, conversation.LastMessageKind,
        conversation.LastMessagePreview, conversation.LastMessageStatus, conversation.LastMessageAt, conversation.UpdatedAt, conversation.Version, conversation.Channel, conversation.ResolutionReasonId, conversation.LastActivityAt,
        conversation.AutomationStatus, conversation.AutomationStartedAt, conversation.AutomationPausedAt, conversation.AutomationPausedByMemberId, conversation.AutomationPauseReason,
        conversation.AutomationChatbotId, conversation.AutomationStep, conversation.AutomationResumedAt,
        conversation.BlockedAt, conversation.BlockedByMemberId);

    public async Task<IReadOnlyList<ConversationRow>> ListAsync(
        Guid tenantId, ConversationListFilter filter, ConversationCursor? after, int take, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var query = dbContext.Conversations.AsNoTracking().Where(conversation => conversation.TenantId == tenantId && conversation.Status == filter.Status);
        // Spec 2026-10-14 §5.2: ausente = todos los canales.
        if (filter.Channel is { } channel)
        {
            query = query.Where(conversation => conversation.Channel == channel);
        }

        // Spec qep-frontend 2026-10-15: una conversación sin cliente no tiene etiquetas y no pasa el filtro.
        if (filter.TagCustomerIds is { } tagged)
        {
            var taggedIds = tagged.ToArray();
            query = query.Where(conversation => conversation.CustomerId != null && taggedIds.Contains(conversation.CustomerId.Value));
        }

        // Spec 2026-10-21 §4.5: ausente = todas, con y sin asistente.
        query = filter.Automation switch
        {
            AutomationFilter.Any => query.Where(conversation => conversation.AutomationStatus != ConversationAutomationStatus.None),
            AutomationFilter.Active => query.Where(conversation => conversation.AutomationStatus == ConversationAutomationStatus.Active),
            AutomationFilter.Paused => query.Where(conversation => conversation.AutomationStatus == ConversationAutomationStatus.Paused),
            AutomationFilter.None => query.Where(conversation => conversation.AutomationStatus == ConversationAutomationStatus.None),
            _ => query,
        };

        if (filter.UnreadOnly)
        {
            query = query.Where(conversation => conversation.UnreadCount > 0);
        }

        query = filter.Assigned switch
        {
            // P15: sin membresía activa, «mías» no encuentra nada.
            AssignedFilter.Mine => filter.MemberId is { } me
                ? query.Where(conversation => conversation.AssignedMemberId == me)
                : query.Where(_ => false),
            AssignedFilter.Unassigned => query.Where(conversation => conversation.AssignedMemberId == null),
            _ => query,
        };
        if (filter.Search is { } search)
        {
            var pattern = "%" + Escape(search) + "%";
            var digits = ConversationSearchTerms.NumberDigits(search);
            var digitsPattern = digits is null ? null : "%" + digits + "%";
            var phones = filter.CustomerWaIds.ToArray();
            var customerIds = filter.CustomerIds.ToArray();
            query = query.Where(conversation =>
                (conversation.ProfileName != null && EF.Functions.ILike(conversation.ProfileName, pattern, "\\"))
                || (conversation.Username != null && EF.Functions.ILike(conversation.Username, pattern, "\\"))
                || (digitsPattern != null && conversation.WaId != null && EF.Functions.Like(conversation.WaId, digitsPattern))
                || (conversation.CustomerId != null && customerIds.Contains(conversation.CustomerId.Value))
                || (conversation.CustomerId == null && conversation.WaId != null && phones.Contains(conversation.WaId)));
        }

        // Comparación de fila de Postgres, el mismo orden que el índice, como el hilo de mensajes (MessageQueries).
        if (after is { } cursor)
        {
            query = filter.Sort == ConversationSort.Oldest
                ? query.Where(conversation => EF.Functions.GreaterThan(
                    ValueTuple.Create(conversation.LastActivityAt, conversation.Id), ValueTuple.Create(cursor.LastActivityAt, cursor.Id)))
                : query.Where(conversation => EF.Functions.LessThan(
                    ValueTuple.Create(conversation.LastActivityAt, conversation.Id), ValueTuple.Create(cursor.LastActivityAt, cursor.Id)));
        }

        // El mismo índice (IX_conversations_tenant_status_activity) sirve a los dos órdenes, leído al revés.
        var ordered = filter.Sort == ConversationSort.Oldest
            ? query.OrderBy(conversation => conversation.LastActivityAt).ThenBy(conversation => conversation.Id)
            : query.OrderByDescending(conversation => conversation.LastActivityAt).ThenByDescending(conversation => conversation.Id);
        return await ordered
            .Take(take)
            .Select(Projection)
            .ToListAsync(cancellationToken);
    }

    public async Task<ConversationCountsDto> CountsAsync(Guid tenantId, Guid? memberId, CancellationToken cancellationToken)
    {
        // Las dos condiciones son literalmente las de los parciales IX_conversations_tenant_open e
        // IX_conversations_tenant_unread: así el planner puede usarlos (index-only scan).
        var open = await dbContext.Conversations.AsNoTracking()
            .CountAsync(conversation => conversation.TenantId == tenantId && conversation.Status == ConversationStatus.Open, cancellationToken);
        var unread = await dbContext.Conversations.AsNoTracking()
            .Where(conversation => conversation.TenantId == tenantId && conversation.UnreadCount > 0)
            .SumAsync(conversation => conversation.UnreadCount, cancellationToken);
        // D-A10: sólo abiertas, por los parciales de asignado (IX_conversations_tenant_assignee/unassigned_status_activity).
        var mine = memberId is { } me
            ? await dbContext.Conversations.AsNoTracking().CountAsync(
                conversation => conversation.TenantId == tenantId && conversation.AssignedMemberId == me && conversation.Status == ConversationStatus.Open, cancellationToken)
            : 0;
        var unassigned = await dbContext.Conversations.AsNoTracking().CountAsync(
            conversation => conversation.TenantId == tenantId && conversation.AssignedMemberId == null && conversation.Status == ConversationStatus.Open, cancellationToken);
        // Misma condición que IX_conversations_tenant_automation_activity (parcial) más el estado abierto.
        var automated = await dbContext.Conversations.AsNoTracking().CountAsync(
            conversation => conversation.TenantId == tenantId && conversation.Status == ConversationStatus.Open
                && conversation.AutomationStatus != ConversationAutomationStatus.None, cancellationToken);
        var automationActive = await dbContext.Conversations.AsNoTracking().CountAsync(
            conversation => conversation.TenantId == tenantId && conversation.Status == ConversationStatus.Open
                && conversation.AutomationStatus == ConversationAutomationStatus.Active, cancellationToken);
        return new ConversationCountsDto(open, unread, mine, unassigned, automated, automationActive);
    }

    public Task<ConversationRow?> FindAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) =>
        dbContext.Conversations.AsNoTracking()
            .Where(conversation => conversation.TenantId == tenantId && conversation.Id == conversationId)
            .Select(Projection)
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<ConversationRow>> FindManyAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        var wanted = ids.ToArray();
        return await dbContext.Conversations.AsNoTracking()
            .Where(conversation => conversation.TenantId == tenantId && wanted.Contains(conversation.Id))
            .Select(Projection)
            .ToListAsync(cancellationToken);
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
}
