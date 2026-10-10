using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Infrastructure.Persistence;

internal sealed class ConversationRepository(MessagingDbContext dbContext) : IConversationRepository
{
    public Task<Conversation?> FindAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) =>
        dbContext.Conversations.SingleOrDefaultAsync(
            conversation => conversation.TenantId == tenantId && conversation.Id == conversationId, cancellationToken);

    public async Task<ReadReceiptTarget?> MarkReadAsync(Guid tenantId, Guid conversationId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Spec 2026-10-09 §8.4: una sentencia, sin comparar version. La fila queda con su candado sólo mientras
        // corre el UPDATE; una ingesta concurrente (§7.5) espera y suma su no leído después, nunca se pierde.
        var rows = await dbContext.Database.SqlQuery<ReadRow>(
            $"""
            UPDATE messaging.conversations
               SET unread_count = 0, updated_at = {now}, version = version + 1
             WHERE id = {conversationId} AND tenant_id = {tenantId} AND unread_count > 0
            RETURNING connection_id AS "ConnectionId", last_inbound_wamid AS "LastInboundWamid", last_inbound_at AS "LastInboundAt"
            """).ToListAsync(cancellationToken);
        return rows.Count == 1 ? new ReadReceiptTarget(rows[0].ConnectionId, rows[0].LastInboundWamid, rows[0].LastInboundAt) : null;
    }

    public Task<bool> ExistsAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) =>
        dbContext.Conversations.AsNoTracking()
            .AnyAsync(conversation => conversation.TenantId == tenantId && conversation.Id == conversationId, cancellationToken);

    private sealed record ReadRow(Guid ConnectionId, string? LastInboundWamid, DateTimeOffset? LastInboundAt);
}
