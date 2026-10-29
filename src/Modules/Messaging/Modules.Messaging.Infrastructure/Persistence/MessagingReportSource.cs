using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>
/// Spec 2026-10-28 «Computation»: extrae las sesiones del periodo y los contactos nuevos con SQL; el tiempo de atención y los percentiles
/// los hace C#. Todo en <c>created_at</c> (hora de llegada a QEP), nunca <c>occurred_at</c>: Meta reenvía tarde.
/// <list type="bullet">
/// <item>Una sesión empieza en <c>conversations.created_at</c> o en un evento <c>Reopened</c> (<c>direction = 3, kind = 13</c>) y termina en el
/// siguiente <c>Reopened</c> de la misma conversación.</item>
/// <item>El entrante de arranque es el primer <c>direction = 1</c> de la sesión; sin él no hay reloj (S3).</item>
/// <item>Cuenta como respuesta un saliente (<c>direction = 2</c>) que no falló (<c>status &lt;&gt; 4</c>, D-6) y que no es de campaña
/// (<c>campaign_recipients.message_id</c>, D-12). La del asesor exige <c>sent_by_kind = 1</c> y autor: el eco de Instagram queda fuera (S6).</item>
/// </list>
/// </summary>
internal sealed class MessagingReportSource(MessagingDbContext dbContext) : IMessagingReportSource
{
    public async Task<ReportData> LoadAsync(
        Guid tenantId, ReportWindow window, ConversationChannel? channel, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        _ = nowUtc;
        var channelName = channel?.ToString();
        var start = window.StartUtc;
        var end = window.EndExclusiveUtc;

        var sessions = await dbContext.Database.SqlQuery<SessionRow>(
            $"""
            WITH starts AS (
                SELECT c.id AS conversation_id, c.connection_id, c.channel, c.assigned_member_id, c.created_at AS session_start
                FROM messaging.conversations c
                WHERE c.tenant_id = {tenantId} AND c.created_at >= {start} AND c.created_at < {end}
                UNION ALL
                SELECT m.conversation_id, c.connection_id, c.channel, c.assigned_member_id, m.created_at
                FROM messaging.messages m
                JOIN messaging.conversations c ON c.id = m.conversation_id
                WHERE m.tenant_id = {tenantId} AND m.direction = 3 AND m.kind = 13 AND m.details->>'type' = 'Reopened'
                  AND m.created_at >= {start} AND m.created_at < {end}
            ),
            sessions AS (
                SELECT s.*, e.at AS session_end
                FROM starts s
                LEFT JOIN LATERAL (
                    SELECT min(r.created_at) AS at FROM messaging.messages r
                    WHERE r.conversation_id = s.conversation_id AND r.tenant_id = {tenantId} AND r.direction = 3 AND r.kind = 13
                      AND r.details->>'type' = 'Reopened' AND r.created_at > s.session_start
                ) e ON true
                WHERE {channelName}::text IS NULL OR s.channel = {channelName}::text
            )
            SELECT s.conversation_id AS "ConversationId", s.connection_id AS "ConnectionId", s.channel AS "Channel",
                   s.assigned_member_id AS "AssignedMemberId", s.session_start AS "SessionStart",
                   si.at AS "StartInboundAt", fo.at AS "FirstAnyOutboundAt", fm.at AS "FirstMemberOutboundAt",
                   fm.member_id AS "FirstMemberId", fe.at AS "FirstInboundEverAt"
            FROM sessions s
            LEFT JOIN LATERAL (
                SELECT min(i.created_at) AS at FROM messaging.messages i
                WHERE i.conversation_id = s.conversation_id AND i.direction = 1 AND i.created_at >= s.session_start
                  AND (s.session_end IS NULL OR i.created_at < s.session_end)
            ) si ON true
            LEFT JOIN LATERAL (
                SELECT min(o.created_at) AS at FROM messaging.messages o
                WHERE si.at IS NOT NULL AND o.conversation_id = s.conversation_id AND o.direction = 2 AND o.status <> 4
                  AND o.created_at > si.at AND (s.session_end IS NULL OR o.created_at < s.session_end)
                  AND NOT EXISTS (SELECT 1 FROM messaging.campaign_recipients cr WHERE cr.message_id = o.id)
            ) fo ON true
            LEFT JOIN LATERAL (
                SELECT o.created_at AS at, o.sent_by_member_id AS member_id FROM messaging.messages o
                WHERE si.at IS NOT NULL AND o.conversation_id = s.conversation_id AND o.direction = 2 AND o.status <> 4
                  AND o.sent_by_kind = 1 AND o.sent_by_member_id IS NOT NULL
                  AND o.created_at > si.at AND (s.session_end IS NULL OR o.created_at < s.session_end)
                  AND NOT EXISTS (SELECT 1 FROM messaging.campaign_recipients cr WHERE cr.message_id = o.id)
                ORDER BY o.created_at, o.id LIMIT 1
            ) fm ON true
            LEFT JOIN LATERAL (
                SELECT min(i.created_at) AS at FROM messaging.messages i
                WHERE i.conversation_id = s.conversation_id AND i.direction = 1
            ) fe ON true
            """).ToListAsync(cancellationToken);

        var newContacts = await dbContext.Database.SqlQuery<NewContactSql>(
            $"""
            SELECT c.id AS "ConversationId", c.channel AS "Channel", f.at AS "FirstInboundAt"
            FROM (
                SELECT DISTINCT m.conversation_id FROM messaging.messages m
                WHERE m.tenant_id = {tenantId} AND m.direction = 1 AND m.created_at >= {start} AND m.created_at < {end}
            ) w
            JOIN messaging.conversations c ON c.id = w.conversation_id
            JOIN LATERAL (
                SELECT min(i.created_at) AS at FROM messaging.messages i WHERE i.conversation_id = c.id AND i.direction = 1
            ) f ON true
            WHERE f.at >= {start} AND f.at < {end}
              AND ({channelName}::text IS NULL OR c.channel = {channelName}::text)
            """).ToListAsync(cancellationToken);

        return new ReportData(
            [.. sessions.Select(row => new ReportSessionRow(
                row.ConversationId, row.ConnectionId, Enum.Parse<ConversationChannel>(row.Channel), row.AssignedMemberId, row.SessionStart,
                row.StartInboundAt, row.FirstAnyOutboundAt, row.FirstMemberOutboundAt, row.FirstMemberId, row.FirstInboundEverAt))],
            [.. newContacts.Select(row => new NewContactRow(row.ConversationId, Enum.Parse<ConversationChannel>(row.Channel), row.FirstInboundAt))]);
    }

    public async Task<IReadOnlyList<ConversationChannel>> ChannelsWithDataAsync(
        Guid tenantId, DateTimeOffset startUtc, DateTimeOffset endExclusiveUtc, CancellationToken cancellationToken)
    {
        // Mismas dos fuentes de sesión que LoadAsync (conversación creada o Reopened) y el mismo created_at, sin el filtro de canal.
        var names = await dbContext.Database.SqlQuery<string>(
            $"""
            SELECT c.channel AS "Value" FROM messaging.conversations c
            WHERE c.tenant_id = {tenantId} AND c.created_at >= {startUtc} AND c.created_at < {endExclusiveUtc}
            UNION
            SELECT c.channel AS "Value" FROM messaging.messages m
            JOIN messaging.conversations c ON c.id = m.conversation_id
            WHERE m.tenant_id = {tenantId} AND m.direction = 3 AND m.kind = 13 AND m.details->>'type' = 'Reopened'
              AND m.created_at >= {startUtc} AND m.created_at < {endExclusiveUtc}
            """).ToListAsync(cancellationToken);
        return [.. names.Select(Enum.Parse<ConversationChannel>)];
    }

    // EF materializa estos tipos por nombre de columna (alias entre comillas).
    private sealed class SessionRow
    {
        public Guid ConversationId { get; set; }
        public Guid ConnectionId { get; set; }
        public string Channel { get; set; } = string.Empty;
        public Guid? AssignedMemberId { get; set; }
        public DateTimeOffset SessionStart { get; set; }
        public DateTimeOffset? StartInboundAt { get; set; }
        public DateTimeOffset? FirstAnyOutboundAt { get; set; }
        public DateTimeOffset? FirstMemberOutboundAt { get; set; }
        public Guid? FirstMemberId { get; set; }
        public DateTimeOffset? FirstInboundEverAt { get; set; }
    }

    private sealed class NewContactSql
    {
        public Guid ConversationId { get; set; }
        public string Channel { get; set; } = string.Empty;
        public DateTimeOffset FirstInboundAt { get; set; }
    }
}
