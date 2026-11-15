using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Domain;
using Modules.Support.Application;
using Modules.Support.Infrastructure.Persistence;

namespace Bootstrapper;

/// <summary>
/// Soporte fase 2 (plan E2): lo que el seguimiento del hilo lee en cada pasada, de todos los tenants, por SQL crudo sobre la conexión de
/// <see cref="SupportDbContext"/>. Mensajería no avisa: un entrante cuenta para un ticket si su hora (<c>occurred_at</c>, no la de
/// llegada) cae en la ventana de seguimiento de un ticket no cerrado de su conversación (plan E4), la misma regla que
/// <c>SupportTicket.CoversInbound</c>. Recorre los mensajes de cada conversación seguida por <c>IX_messages_thread</c>
/// (<c>conversation_id, occurred_at</c>) y descarta los ya vistos por <c>IX_ticket_messages_conversation</c> y la PK de
/// <c>unclassified_messages</c>.
///
/// **Lo que lee por nombre.** <c>support.tickets</c> (<c>tenant_id</c>, <c>conversation_id</c>, <c>status</c>, <c>follow_since</c>,
/// <c>follow_off_at</c>), <c>support.ticket_messages</c> (<c>tenant_id</c>, <c>conversation_id</c>, <c>message_id</c>),
/// <c>support.unclassified_messages</c> (<c>message_id</c>, <c>tenant_id</c>, <c>conversation_id</c>, <c>resolution</c>) y
/// <c>messaging.messages</c> (<c>id</c>, <c>tenant_id</c>, <c>conversation_id</c>, <c>occurred_at</c>, <c>direction</c>,
/// <c>imported</c>). Renombrar una compila y deja el worker fallando en cada tick sin vincular nada; lo amarra
/// <c>SupportBootstrapperAdaptersTests</c>.
/// </summary>
internal sealed class SupportThreadFeed(SupportDbContext support) : ISupportThreadFeed
{
    private const short InboundDirection = (short)MessageDirection.Inbound;

    public async Task<IReadOnlyList<SupportPendingInbound>> ListPendingAsync(int take, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);
        var rows = await support.Database.SqlQuery<PendingRow>($"""
            SELECT m.tenant_id AS "TenantId", m.conversation_id AS "ConversationId", m.id AS "MessageId", m.occurred_at AS "OccurredAt"
            FROM messaging.messages m
            WHERE m.direction = {InboundDirection}
              AND NOT m.imported
              AND EXISTS (
                  SELECT 1 FROM support.tickets t
                  WHERE t.tenant_id = m.tenant_id
                    AND t.conversation_id = m.conversation_id
                    AND t.status <> 'Closed'
                    AND t.follow_since IS NOT NULL
                    AND t.follow_since <= m.occurred_at
                    AND (t.follow_off_at IS NULL OR m.occurred_at < t.follow_off_at))
              AND NOT EXISTS (
                  SELECT 1 FROM support.ticket_messages link
                  WHERE link.tenant_id = m.tenant_id AND link.conversation_id = m.conversation_id AND link.message_id = m.id)
              AND NOT EXISTS (SELECT 1 FROM support.unclassified_messages pending WHERE pending.message_id = m.id)
              AND m.conversation_id IN (
                  SELECT conversation_id FROM support.tickets
                  WHERE status <> 'Closed' AND follow_since IS NOT NULL AND conversation_id IS NOT NULL)
            ORDER BY m.occurred_at, m.id
            LIMIT {take}
            """).ToListAsync(cancellationToken);
        return rows.Select(row => new SupportPendingInbound(row.TenantId, row.ConversationId, row.MessageId, row.OccurredAt)).ToList();
    }

    public async Task<IReadOnlyList<SupportConversationKey>> ListConversationsWithPendingUnclassifiedAsync(
        int take, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);
        var rows = await support.Database.SqlQuery<ConversationRow>($"""
            SELECT tenant_id AS "TenantId", conversation_id AS "ConversationId"
            FROM support.unclassified_messages
            WHERE resolution IS NULL
            GROUP BY tenant_id, conversation_id
            ORDER BY min(occurred_at), conversation_id
            LIMIT {take}
            """).ToListAsync(cancellationToken);
        return rows.Select(row => new SupportConversationKey(row.TenantId, row.ConversationId)).ToList();
    }

    private sealed class PendingRow
    {
        public Guid TenantId { get; init; }

        public Guid ConversationId { get; init; }

        public Guid MessageId { get; init; }

        public DateTimeOffset OccurredAt { get; init; }
    }

    private sealed class ConversationRow
    {
        public Guid TenantId { get; init; }

        public Guid ConversationId { get; init; }
    }
}
