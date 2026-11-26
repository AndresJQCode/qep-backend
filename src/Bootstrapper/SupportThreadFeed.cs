using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Modules.Messaging.Domain;
using Modules.Support.Application;
using Modules.Support.Infrastructure;
using Modules.Support.Infrastructure.Persistence;

namespace Bootstrapper;

/// <summary>
/// Soporte fase 2 (plan E2): lo que el seguimiento del hilo lee en cada pasada, de todos los tenants, por SQL crudo sobre la conexión de
/// <see cref="SupportDbContext"/>. Mensajería no avisa: un entrante cuenta para un ticket si su hora (<c>occurred_at</c>, no la de
/// llegada) cae en la ventana de seguimiento de un ticket no cerrado de su conversación (plan E4), la misma regla que
/// <c>SupportTicket.CoversInbound</c>. Recorre los mensajes de cada conversación seguida por <c>IX_messages_thread</c>
/// (<c>conversation_id, occurred_at</c>) y descarta los ya vistos por <c>IX_ticket_messages_conversation</c>
/// (<c>tenant_id, conversation_id, message_id</c>) y la PK de <c>unclassified_messages</c>.
///
/// **Lookback (revisión final, I2).** Las dos lecturas miran sólo lo que llegó en las últimas <c>Support:Workers:FeedLookbackHours</c>
/// (24 por defecto), por <c>created_at</c>: sin eso, una conversación seguida se recorría entera desde <c>follow_since</c> en cada tick,
/// para siempre. La ventana del ticket se sigue midiendo con <c>occurred_at</c>. Un worker caído más que el lookback deja sin vincular lo
/// que llegó mientras tanto.
///
/// **Lo que lee por nombre.** <c>support.tickets</c> (<c>tenant_id</c>, <c>conversation_id</c>, <c>status</c>, <c>follow_since</c>,
/// <c>follow_off_at</c>), <c>support.ticket_messages</c> (<c>tenant_id</c>, <c>conversation_id</c>, <c>message_id</c>),
/// <c>support.unclassified_messages</c> (<c>message_id</c>, <c>tenant_id</c>, <c>conversation_id</c>, <c>resolution</c>,
/// <c>created_at</c>) y <c>messaging.messages</c> (<c>id</c>, <c>tenant_id</c>, <c>conversation_id</c>, <c>occurred_at</c>,
/// <c>created_at</c>, <c>direction</c>, <c>imported</c>). Renombrar una compila y deja el worker fallando en cada tick sin vincular nada;
/// lo amarra <c>SupportBootstrapperAdaptersTests</c>.
/// </summary>
internal sealed class SupportThreadFeed(SupportDbContext support, IOptions<SupportWorkerOptions> options) : ISupportThreadFeed
{
    private const short InboundDirection = (short)MessageDirection.Inbound;

    private TimeSpan Lookback => TimeSpan.FromHours(Math.Max(1, options.Value.FeedLookbackHours));

    /// <summary>Cuánto después de apagar «Seguir el hilo» se sigue recorriendo la conversación por los entrantes de antes del apagado.
    /// El worker corre cada pocos segundos: una hora sobra.</summary>
    internal static readonly TimeSpan FollowOffGrace = TimeSpan.FromHours(1);

    public async Task<IReadOnlyList<SupportPendingInbound>> ListPendingAsync(int take, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);
        var lookback = Lookback;
        // El CTE da, por conversación seguida, el follow_since más viejo: así cada conversación recorre IX_messages_thread
        // (conversation_id, occurred_at) por rango y no desde su primer mensaje. Entran los tickets que siguen el hilo y los que lo
        // apagaron hace menos de FollowOffGrace: un entrante que llegó antes de apagar todavía se vincula en el tick siguiente, y un
        // ticket apagado hace días no hace que su conversación se recorra en cada tick.
        var rows = await support.Database.SqlQuery<PendingRow>($"""
            WITH followed AS (
                SELECT tenant_id, conversation_id, min(follow_since) AS since
                FROM support.tickets
                WHERE status <> 'Closed'
                  AND conversation_id IS NOT NULL
                  AND follow_since IS NOT NULL
                  AND (follow_thread OR follow_off_at >= now() - {FollowOffGrace})
                GROUP BY tenant_id, conversation_id)
            SELECT m.tenant_id AS "TenantId", m.conversation_id AS "ConversationId", m.id AS "MessageId", m.occurred_at AS "OccurredAt"
            FROM followed f
            JOIN messaging.messages m
              ON m.conversation_id = f.conversation_id AND m.tenant_id = f.tenant_id AND m.occurred_at >= f.since
            WHERE m.direction = {InboundDirection}
              AND NOT m.imported
              AND m.created_at >= now() - {lookback}
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
            ORDER BY m.occurred_at, m.id
            LIMIT {take}
            """).ToListAsync(cancellationToken);
        return rows.Select(row => new SupportPendingInbound(row.TenantId, row.ConversationId, row.MessageId, row.OccurredAt)).ToList();
    }

    // Sólo las filas que hoy tienen exactamente un candidato: las que esperan al asesor (dos o más) o se quedaron sin ninguno (todos
    // cerrados) no salen, así no ocupan la cabeza del lote en cada tick y no matan de hambre a las demás. El conteo es la misma regla de
    // ventana que SupportTicket.CoversInbound.
    public async Task<IReadOnlyList<SupportPendingInbound>> ListReassignableUnclassifiedAsync(
        int take, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);
        var lookback = Lookback;
        var rows = await support.Database.SqlQuery<PendingRow>($"""
            SELECT u.tenant_id AS "TenantId", u.conversation_id AS "ConversationId", u.message_id AS "MessageId",
                   u.occurred_at AS "OccurredAt"
            FROM support.unclassified_messages u
            WHERE u.resolution IS NULL
              AND u.created_at >= now() - {lookback}
              AND (SELECT count(*) FROM support.tickets t
                   WHERE t.tenant_id = u.tenant_id
                     AND t.conversation_id = u.conversation_id
                     AND t.status <> 'Closed'
                     AND t.follow_since IS NOT NULL
                     AND t.follow_since <= u.occurred_at
                     AND (t.follow_off_at IS NULL OR u.occurred_at < t.follow_off_at)) = 1
            ORDER BY u.occurred_at, u.message_id
            LIMIT {take}
            """).ToListAsync(cancellationToken);
        return rows.Select(row => new SupportPendingInbound(row.TenantId, row.ConversationId, row.MessageId, row.OccurredAt)).ToList();
    }

    private sealed class PendingRow
    {
        public Guid TenantId { get; init; }

        public Guid ConversationId { get; init; }

        public Guid MessageId { get; init; }

        public DateTimeOffset OccurredAt { get; init; }
    }
}
