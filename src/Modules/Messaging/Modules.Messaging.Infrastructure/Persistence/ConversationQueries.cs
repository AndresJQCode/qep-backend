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
        conversation.Id, conversation.TenantId, conversation.ConnectionId, conversation.WaId, conversation.ProfileName, conversation.Status,
        conversation.UnreadCount, conversation.LastInboundAt, conversation.LastMessageId, conversation.LastMessageDirection, conversation.LastMessageKind,
        conversation.LastMessagePreview, conversation.LastMessageStatus, conversation.LastMessageAt, conversation.UpdatedAt, conversation.Version);

    public async Task<(IReadOnlyList<ConversationRow> Items, int Total)> ListAsync(
        Guid tenantId, ConversationStatus status, string? search, IReadOnlyCollection<string> customerWaIds, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = dbContext.Conversations.AsNoTracking().Where(conversation => conversation.TenantId == tenantId && conversation.Status == status);
        if (search is not null)
        {
            var pattern = "%" + Escape(search) + "%";
            var digits = ConversationSearchTerms.NumberDigits(search);
            var digitsPattern = digits is null ? null : "%" + digits + "%";
            var phones = customerWaIds.ToArray();
            query = query.Where(conversation =>
                (conversation.ProfileName != null && EF.Functions.ILike(conversation.ProfileName, pattern, "\\"))
                || (digitsPattern != null && EF.Functions.Like(conversation.WaId, digitsPattern))
                || phones.Contains(conversation.WaId));
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(conversation => conversation.LastActivityAt).ThenByDescending(conversation => conversation.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(Projection)
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    public async Task<ConversationCountsDto> CountsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        // Las dos condiciones son literalmente las de los parciales IX_conversations_tenant_open e
        // IX_conversations_tenant_unread: así el planner puede usarlos (index-only scan).
        var open = await dbContext.Conversations.AsNoTracking()
            .CountAsync(conversation => conversation.TenantId == tenantId && conversation.Status == ConversationStatus.Open, cancellationToken);
        var unread = await dbContext.Conversations.AsNoTracking()
            .Where(conversation => conversation.TenantId == tenantId && conversation.UnreadCount > 0)
            .SumAsync(conversation => conversation.UnreadCount, cancellationToken);
        return new ConversationCountsDto(open, unread);
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
