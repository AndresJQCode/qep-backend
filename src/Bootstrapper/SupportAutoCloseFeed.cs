using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Domain;
using Modules.Support.Application;
using Modules.Support.Infrastructure.Persistence;

namespace Bootstrapper;

/// <summary>
/// Fase 4b, Tarea 14 (H22, owner 2026-11-16, Q-4): lo que el cierre automático lee, de todos los tenants, por SQL crudo sobre la conexión
/// de <see cref="SupportDbContext"/>. La última respuesta del cliente es el <c>occurred_at</c> más reciente, posterior a la resolución, de
/// un vínculo del ticket que está **ahora** <c>Linked</c> o <c>Pinned</c> (soltarlo lo saca, y la fecha vuelve) sobre un entrante en vivo
/// (<c>direction</c> entrante, no <c>imported</c>) que no es una respuesta de la encuesta del ticket: ni el toque que cita una encuesta
/// (<c>kind</c> <c>Interactive</c> con <c>reply_to_message_id</c> = su <c>message_id</c>) ni el que quedó como su calificación o su
/// comentario. Recorre los vínculos del ticket por <c>IX_ticket_messages_ticket_visible</c> (<c>ticket_id, occurred_at</c>, sin los
/// soltados).
///
/// **Lo que lee por nombre.** <c>support.tickets</c> (<c>id</c>, <c>tenant_id</c>, <c>status</c>, <c>resolved_at</c>),
/// <c>support.ticket_messages</c> (<c>tenant_id</c>, <c>ticket_id</c>, <c>message_id</c>, <c>state</c>, <c>occurred_at</c>),
/// <c>support.ticket_surveys</c> (<c>tenant_id</c>, <c>ticket_id</c>, <c>message_id</c>, <c>rating_message_id</c>,
/// <c>comment_message_id</c>) y <c>messaging.messages</c> (<c>id</c>, <c>tenant_id</c>, <c>direction</c>, <c>kind</c>,
/// <c>reply_to_message_id</c>, <c>imported</c>). Renombrar una compila y deja el cierre sin ver respuestas (cierra antes de tiempo) o
/// fallando en cada tick; lo amarra <c>SupportBootstrapperAdaptersTests.TheAutoCloseFeedRunsAgainstTheMigratedSchema</c>.
/// </summary>
internal sealed class SupportAutoCloseFeed(SupportDbContext support) : ISupportAutoCloseFeed
{
    private const short InboundDirection = (short)MessageDirection.Inbound;

    private const short InteractiveKind = (short)MessageKind.Interactive;

    public async Task<IReadOnlyList<SupportAutoCloseCandidate>> ListCandidatesAsync(
        int take, SupportAutoCloseCandidate? after, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);
        // Dos textos y no un parámetro nulo: PostgreSQL no tipa un NULL suelto.
        var rows = after is null
            ? await support.Database.SqlQuery<CandidateRow>($"""
                SELECT t.tenant_id AS "TenantId", t.id AS "TicketId", t.resolved_at AS "ResolvedAt",
                       (SELECT max(tm.occurred_at)
                        FROM support.ticket_messages tm
                        JOIN messaging.messages m ON m.id = tm.message_id AND m.tenant_id = tm.tenant_id
                        WHERE tm.tenant_id = t.tenant_id
                          AND tm.ticket_id = t.id
                          AND tm.state <> 'Released'
                          AND tm.occurred_at > t.resolved_at
                          AND m.direction = {InboundDirection}
                          AND NOT m.imported
                          AND NOT EXISTS (
                              SELECT 1 FROM support.ticket_surveys s
                              WHERE s.tenant_id = t.tenant_id AND s.ticket_id = t.id
                                AND (s.rating_message_id = m.id
                                     OR s.comment_message_id = m.id
                                     OR (m.kind = {InteractiveKind} AND m.reply_to_message_id = s.message_id)))) AS "LastCustomerReplyAt"
                FROM support.tickets t
                WHERE t.status = 'Resolved'
                  AND t.resolved_at IS NOT NULL
                  AND t.resolved_at < now() - interval '1 day'
                ORDER BY t.resolved_at, t.id
                LIMIT {take}
                """).ToListAsync(cancellationToken)
            : await support.Database.SqlQuery<CandidateRow>($"""
                SELECT t.tenant_id AS "TenantId", t.id AS "TicketId", t.resolved_at AS "ResolvedAt",
                       (SELECT max(tm.occurred_at)
                        FROM support.ticket_messages tm
                        JOIN messaging.messages m ON m.id = tm.message_id AND m.tenant_id = tm.tenant_id
                        WHERE tm.tenant_id = t.tenant_id
                          AND tm.ticket_id = t.id
                          AND tm.state <> 'Released'
                          AND tm.occurred_at > t.resolved_at
                          AND m.direction = {InboundDirection}
                          AND NOT m.imported
                          AND NOT EXISTS (
                              SELECT 1 FROM support.ticket_surveys s
                              WHERE s.tenant_id = t.tenant_id AND s.ticket_id = t.id
                                AND (s.rating_message_id = m.id
                                     OR s.comment_message_id = m.id
                                     OR (m.kind = {InteractiveKind} AND m.reply_to_message_id = s.message_id)))) AS "LastCustomerReplyAt"
                FROM support.tickets t
                WHERE t.status = 'Resolved'
                  AND t.resolved_at IS NOT NULL
                  AND t.resolved_at < now() - interval '1 day'
                  AND (t.resolved_at, t.id) > ({after.ResolvedAt.ToUniversalTime()}, {after.TicketId})
                ORDER BY t.resolved_at, t.id
                LIMIT {take}
                """).ToListAsync(cancellationToken);
        return rows
            .Select(row => new SupportAutoCloseCandidate(row.TenantId, row.TicketId, row.ResolvedAt, row.LastCustomerReplyAt))
            .ToList();
    }

    public async Task<DateTimeOffset?> LastCustomerReplyAsync(
        Guid tenantId, Guid ticketId, DateTimeOffset after, CancellationToken cancellationToken)
    {
        var at = after.ToUniversalTime();
        var rows = await support.Database.SqlQuery<ReplyRow>($"""
            SELECT max(tm.occurred_at) AS "LastCustomerReplyAt"
            FROM support.ticket_messages tm
            JOIN messaging.messages m ON m.id = tm.message_id AND m.tenant_id = tm.tenant_id
            WHERE tm.tenant_id = {tenantId}
              AND tm.ticket_id = {ticketId}
              AND tm.state <> 'Released'
              AND tm.occurred_at > {at}
              AND m.direction = {InboundDirection}
              AND NOT m.imported
              AND NOT EXISTS (
                  SELECT 1 FROM support.ticket_surveys s
                  WHERE s.tenant_id = {tenantId} AND s.ticket_id = {ticketId}
                    AND (s.rating_message_id = m.id
                         OR s.comment_message_id = m.id
                         OR (m.kind = {InteractiveKind} AND m.reply_to_message_id = s.message_id)))
            """).ToListAsync(cancellationToken);
        return rows.SingleOrDefault()?.LastCustomerReplyAt;
    }

    private sealed class CandidateRow
    {
        public Guid TenantId { get; init; }

        public Guid TicketId { get; init; }

        public DateTimeOffset ResolvedAt { get; init; }

        public DateTimeOffset? LastCustomerReplyAt { get; init; }
    }

    private sealed class ReplyRow
    {
        public DateTimeOffset? LastCustomerReplyAt { get; init; }
    }
}
