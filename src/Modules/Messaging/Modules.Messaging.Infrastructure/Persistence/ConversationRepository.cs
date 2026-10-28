using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using Modules.Messaging.Infrastructure.Webhook;

namespace Modules.Messaging.Infrastructure.Persistence;

internal sealed class ConversationRepository(MessagingDbContext dbContext, IMessagingAuditRecorder audit) : IConversationRepository
{
    public Task<Conversation?> FindAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) =>
        dbContext.Conversations.SingleOrDefaultAsync(
            conversation => conversation.TenantId == tenantId && conversation.Id == conversationId, cancellationToken);

    public async Task<bool> SetAutomationStepAsync(Guid tenantId, Guid conversationId, string? stepTitle, CancellationToken cancellationToken)
    {
        var step = Conversation.FitAutomationStep(stepTitle);
        return await dbContext.Database.ExecuteSqlAsync(
            $"""
            UPDATE messaging.conversations SET automation_step = {step}
             WHERE id = {conversationId} AND tenant_id = {tenantId} AND automation_status <> 'None'
            """, cancellationToken) > 0;
    }

    public async Task<Guid?> LinkCustomerAsync(
        Guid tenantId, Guid conversationId, Guid customerId, bool created, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var linked = await dbContext.Database.SqlQuery<LinkedRow>(
            $"""
            UPDATE messaging.conversations SET customer_id = {customerId}
             WHERE id = {conversationId} AND tenant_id = {tenantId} AND customer_id IS NULL
            RETURNING connection_id AS "ConnectionId"
            """).ToListAsync(cancellationToken);
        if (linked.Count == 1)
        {
            await ConversationEventRows.InsertAsync(dbContext, tenantId, linked[0].ConnectionId, conversationId,
                new ConversationEvent(created ? ConversationEventType.CustomerCreated : ConversationEventType.CustomerLinked, CustomerId: customerId),
                now, now, cancellationToken);
        }

        var current = await dbContext.Database.SqlQuery<Guid>(
            $"""
            SELECT customer_id AS "Value" FROM messaging.conversations
             WHERE id = {conversationId} AND tenant_id = {tenantId} AND customer_id IS NOT NULL
            """).ToListAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return current.Count == 0 ? null : current[0];
    }

    private sealed record LinkedRow(Guid ConnectionId);

    public async Task<bool> SetAutomationChatbotAsync(Guid tenantId, Guid conversationId, Guid chatbotId, CancellationToken cancellationToken) =>
        await dbContext.Database.ExecuteSqlAsync(
            $"""
            UPDATE messaging.conversations SET automation_chatbot_id = {chatbotId}
             WHERE id = {conversationId} AND tenant_id = {tenantId} AND automation_status = 'Active'
            """, cancellationToken) > 0;

    public async Task<ReadReceiptTarget?> MarkReadAsync(Guid tenantId, Guid conversationId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Spec 2026-10-09 §8.4: una sentencia, sin comparar version. La fila queda con su candado sólo mientras
        // corre el UPDATE; una ingesta concurrente (§7.5) espera y suma su no leído después, nunca se pierde.
        var rows = await dbContext.Database.SqlQuery<ReadRow>(
            $"""
            UPDATE messaging.conversations
               SET unread_count = 0, updated_at = {now}, version = version + 1
             WHERE id = {conversationId} AND tenant_id = {tenantId} AND unread_count > 0
            RETURNING connection_id AS "ConnectionId", last_inbound_wamid AS "LastInboundWamid", last_inbound_at AS "LastInboundAt", channel AS "Channel", user_id AS "UserId"
            """).ToListAsync(cancellationToken);
        if (rows.Count != 1)
        {
            return null;
        }

        var row = rows[0];
        var channel = Enum.TryParse<ConversationChannel>(row.Channel, out var parsed) ? parsed : ConversationChannel.WhatsApp;
        return new ReadReceiptTarget(row.ConnectionId, row.LastInboundWamid, row.LastInboundAt, channel, row.UserId);
    }

    public async Task<RepliedTarget?> MarkRepliedAsync(Guid tenantId, Guid conversationId, int seen, CancellationToken cancellationToken)
    {
        if (seen <= 0)
        {
            return null;
        }

        // Spec 2026-10-16 §3.1: resta lo que el asesor vio, no lo pone en 0: un entrante que llegó durante el envío
        // sobrevive. Sin version ni updated_at a propósito: el envío nunca los sube (Fix 1, hallazgo 1) y bumpearlos
        // aquí le haría perder la concurrencia optimista a quien edita la conversación en paralelo.
        var rows = await dbContext.Database.SqlQuery<RepliedRow>(
            $"""
            UPDATE messaging.conversations
               SET unread_count = unread_count - {seen}
             WHERE id = {conversationId} AND tenant_id = {tenantId} AND unread_count >= {seen}
            RETURNING connection_id AS "ConnectionId", last_inbound_wamid AS "LastInboundWamid", last_inbound_at AS "LastInboundAt", channel AS "Channel", user_id AS "UserId", unread_count AS "UnreadCount"
            """).ToListAsync(cancellationToken);
        if (rows.Count != 1)
        {
            return null;
        }

        var row = rows[0];
        var channel = Enum.TryParse<ConversationChannel>(row.Channel, out var parsed) ? parsed : ConversationChannel.WhatsApp;
        return new RepliedTarget(new ReadReceiptTarget(row.ConnectionId, row.LastInboundWamid, row.LastInboundAt, channel, row.UserId), row.UnreadCount);
    }

    public async Task<bool> MarkUnreadAsync(Guid tenantId, Guid conversationId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Spec 2026-10-14 §3.2: una sentencia, como el read. El 1 es el mismo número que dejaría un entrante nuevo,
        // así que la fila, el badge y counts.unread lo dibujan sin saber que fue una marca manual.
        var rows = await dbContext.Database.ExecuteSqlAsync(
            $"""
            UPDATE messaging.conversations
               SET unread_count = 1, updated_at = {now}, version = version + 1
             WHERE id = {conversationId} AND tenant_id = {tenantId} AND unread_count = 0
            """, cancellationToken);
        return rows == 1;
    }

    public async Task<UnreadFromOutcome> MarkUnreadFromAsync(Guid tenantId, Guid conversationId, Guid messageId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var anchored = await dbContext.Messages.AsNoTracking().AnyAsync(
            message => message.TenantId == tenantId && message.ConversationId == conversationId && message.Id == messageId, cancellationToken);
        if (!anchored)
        {
            return UnreadFromOutcome.MessageNotFound;
        }

        // Spec 2026-10-14 §7.1: «desde» es el orden del hilo (IX_messages_thread), y cuentan sólo los entrantes, como
        // en la ingesta. GREATEST por lo mismo que en MarkUnreadAsync: pisar no leídos reales mentiría.
        var inbound = MessageColumnCodes.ToCode(MessageDirection.Inbound);
        var rows = await dbContext.Database.ExecuteSqlAsync(
            $"""
            UPDATE messaging.conversations AS conversation
               SET unread_count = pending.count, updated_at = {now}, version = conversation.version + 1
              FROM (SELECT count(*)::int AS count
                      FROM messaging.messages AS message
                      JOIN messaging.messages AS anchor ON anchor.id = {messageId}
                     WHERE message.conversation_id = {conversationId}
                       AND message.direction = {inbound}
                       AND (message.occurred_at, message.id) >= (anchor.occurred_at, anchor.id)) AS pending
             WHERE conversation.id = {conversationId} AND conversation.tenant_id = {tenantId}
               AND pending.count > conversation.unread_count
            """, cancellationToken);
        return rows == 1 ? UnreadFromOutcome.Marked : UnreadFromOutcome.Unchanged;
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
            // Spec 2026-10-21 §4.4: si el asistente estaba activo, la misma sentencia lo pausa (AdvisorReplied): asignar sin pausar
            // viola CK_conversations_automation_assignment. Los CASE miran el valor ANTERIOR de automation_status; RETURNING ve
            // el nuevo, y por eso PausedNow se deduce de la razón y la hora que acaba de escribir (COALESCE: sin asistente son NULL).
            var taken = await dbContext.Database.SqlQuery<AutoAssignRow>(
                $"""
                UPDATE messaging.conversations
                   SET assigned_member_id = {memberId}, assigned_at = {at}, version = version + 1, updated_at = {at},
                       automation_status = CASE WHEN automation_status = 'Active' THEN 'Paused' ELSE automation_status END,
                       automation_paused_at = CASE WHEN automation_status = 'Active' THEN {at} ELSE automation_paused_at END,
                       automation_paused_by_member_id = CASE WHEN automation_status = 'Active' THEN {memberId} ELSE automation_paused_by_member_id END,
                       automation_pause_reason = CASE WHEN automation_status = 'Active' THEN 'AdvisorReplied' ELSE automation_pause_reason END
                 WHERE id = {conversationId} AND tenant_id = {tenantId} AND assigned_member_id IS NULL
                RETURNING connection_id AS "ConnectionId",
                          COALESCE(automation_pause_reason = 'AdvisorReplied' AND automation_paused_at = {at}, false) AS "PausedNow"
                """).ToListAsync(cancellationToken);
            if (taken.Count == 1)
            {
                // occurred_at = la hora de la autoasignación, anterior a la respuesta de Meta: el evento queda antes
                // del saliente en el hilo.
                await Webhook.ConversationEventRows.InsertAsync(dbContext, tenantId, taken[0].ConnectionId, conversationId,
                    new ConversationEvent(ConversationEventType.AutoTaken, Actor: memberId), at, at, cancellationToken);
                audit.Record(tenantId, actorUserId, MessagingAuditActions.AutoTaken, conversationId, at);
                if (taken[0].PausedNow)
                {
                    // Un microsegundo después del AutoTaken: dos eventos del mismo cambio deben leerse en orden y los GUID v7
                    // no lo garantizan dentro del mismo milisegundo.
                    await Webhook.ConversationEventRows.InsertAsync(dbContext, tenantId, taken[0].ConnectionId, conversationId,
                        new ConversationEvent(ConversationEventType.AutomationPaused, Actor: memberId, PauseReason: AutomationPauseReason.AdvisorReplied), at.AddTicks(10), at, cancellationToken);
                    audit.Record(tenantId, actorUserId, MessagingAuditActions.AutomationPaused, conversationId, at);
                }

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

    private sealed record AutoAssignRow(Guid ConnectionId, bool PausedNow);

    private sealed record RepliedRow(Guid ConnectionId, string? LastInboundWamid, DateTimeOffset? LastInboundAt, string Channel, string? UserId, int UnreadCount);

    private sealed record ReadRow(Guid ConnectionId, string? LastInboundWamid, DateTimeOffset? LastInboundAt, string Channel, string? UserId);
}
