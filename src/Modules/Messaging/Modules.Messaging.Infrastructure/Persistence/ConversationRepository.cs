using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Infrastructure.Persistence;

internal sealed class ConversationRepository(MessagingDbContext dbContext, IMessagingAuditRecorder audit) : IConversationRepository
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

    public async Task<AutoAssignOutcome> TryAutoAssignAsync(
        Guid tenantId, Guid conversationId, Guid memberId, Guid actorUserId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var at = now.ToUniversalTime();
        // Dos vueltas: si entre el UPDATE y la relectura alguien liberó, la segunda la toma.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            var connection = await dbContext.Database.SqlQuery<Guid>(
                $"""
                UPDATE messaging.conversations
                   SET assigned_member_id = {memberId}, assigned_at = {at}, version = version + 1, updated_at = {at}
                 WHERE id = {conversationId} AND tenant_id = {tenantId} AND assigned_member_id IS NULL
                RETURNING connection_id AS "Value"
                """).ToListAsync(cancellationToken);
            if (connection.Count == 1)
            {
                // occurred_at = la hora de la autoasignación, anterior a la respuesta de Meta: el evento queda antes
                // del saliente en el hilo.
                await Webhook.ConversationEventRows.InsertAsync(dbContext, tenantId, connection[0], conversationId,
                    new ConversationEvent(ConversationEventType.AutoTaken, Actor: memberId), at, at, cancellationToken);
                audit.Record(tenantId, actorUserId, MessagingAuditActions.AutoTaken, conversationId, at);
                await dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return AutoAssignOutcome.Assigned;
            }

            await transaction.RollbackAsync(cancellationToken);
            var current = await dbContext.Conversations.AsNoTracking()
                .Where(conversation => conversation.Id == conversationId && conversation.TenantId == tenantId)
                .Select(conversation => conversation.AssignedMemberId)
                .SingleAsync(cancellationToken);
            if (current == memberId)
            {
                return AutoAssignOutcome.AlreadyMine;
            }

            if (current is not null)
            {
                return AutoAssignOutcome.AssignedToOther;
            }
        }

        return AutoAssignOutcome.AssignedToOther;
    }

    private sealed record ReadRow(Guid ConnectionId, string? LastInboundWamid, DateTimeOffset? LastInboundAt);
}
