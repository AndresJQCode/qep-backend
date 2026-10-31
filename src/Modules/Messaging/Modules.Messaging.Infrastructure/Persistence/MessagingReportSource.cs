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
/// (<c>campaign_recipients.message_id</c>, D-12) ni de una clave de API (<c>sent_by_kind = 4</c>, spec 2026-10-30 D13: es automatización, no atención;
/// su conversación sí cuenta como sesión). La del asesor exige <c>sent_by_kind = 1</c> y autor: el eco de Instagram queda fuera (S6).</item>
/// <item>El historial importado por coexistencia (<c>imported</c>, decisión del owner del 2026-10-30) no es actividad del periodo: su
/// <c>created_at</c> es la hora de la importación. Una conversación importada no arranca sesión al crearse: la arranca su primer entrante
/// en vivo, salvo que ya la haya arrancado un <c>Reopened</c>. Un mensaje importado nunca es el entrante de arranque ni una respuesta.
/// Para «primer entrante» (contacto nuevo o que vuelve) sí cuenta, con su <c>occurred_at</c>, que es la única hora real que tiene: un
/// contacto con historial no es nuevo por escribir en vivo después de la conexión.</item>
/// <item>Una conversación que nació de un eco (<c>started_by_echo</c>) tampoco arranca sesión al crearse: nace cerrada, y la arranca el
/// <c>Reopened</c> del contacto. Una que abre un asesor desde QEP sí cuenta desde su creación, aunque no tenga entrante (S3): nace
/// abierta y nadie la reabre. Por eso el filtro es por origen y no por «tiene entrante».</item>
/// </list>
/// Las tres consultas leen <c>imported</c> y <c>started_by_echo</c> por nombre: renombrarlas compila y deja el reporte en 500; lo amarra
/// <c>CoexistenceReportTests</c>, que corre contra el esquema migrado.
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
                WHERE c.tenant_id = {tenantId} AND c.created_at >= {start} AND c.created_at < {end} AND NOT c.imported AND NOT c.started_by_echo
                UNION ALL
                SELECT m.conversation_id, c.connection_id, c.channel, c.assigned_member_id, m.created_at
                FROM messaging.messages m
                JOIN messaging.conversations c ON c.id = m.conversation_id
                WHERE m.tenant_id = {tenantId} AND m.direction = 3 AND m.kind = 13 AND m.details->>'type' = 'Reopened'
                  AND m.created_at >= {start} AND m.created_at < {end}
                UNION ALL
                SELECT c.id, c.connection_id, c.channel, c.assigned_member_id, fl.at
                FROM messaging.conversations c
                JOIN LATERAL (
                    SELECT min(l.created_at) AS at FROM messaging.messages l
                    WHERE l.conversation_id = c.id AND l.direction = 1 AND NOT l.imported
                ) fl ON true
                WHERE c.tenant_id = {tenantId} AND c.imported AND fl.at >= {start} AND fl.at < {end}
                  AND NOT EXISTS (
                      SELECT 1 FROM messaging.messages r
                      WHERE r.conversation_id = c.id AND r.direction = 3 AND r.kind = 13 AND r.details->>'type' = 'Reopened'
                        AND r.created_at <= fl.at)
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
                WHERE i.conversation_id = s.conversation_id AND i.direction = 1 AND NOT i.imported AND i.created_at >= s.session_start
                  AND (s.session_end IS NULL OR i.created_at < s.session_end)
            ) si ON true
            LEFT JOIN LATERAL (
                SELECT min(o.created_at) AS at FROM messaging.messages o
                WHERE si.at IS NOT NULL AND o.conversation_id = s.conversation_id AND o.direction = 2 AND o.status <> 4 AND NOT o.imported
                  AND o.sent_by_kind <> 4
                  AND o.created_at > si.at AND (s.session_end IS NULL OR o.created_at < s.session_end)
                  AND NOT EXISTS (SELECT 1 FROM messaging.campaign_recipients cr WHERE cr.message_id = o.id)
            ) fo ON true
            LEFT JOIN LATERAL (
                SELECT o.created_at AS at, o.sent_by_member_id AS member_id FROM messaging.messages o
                WHERE si.at IS NOT NULL AND o.conversation_id = s.conversation_id AND o.direction = 2 AND o.status <> 4 AND NOT o.imported
                  AND o.sent_by_kind = 1 AND o.sent_by_member_id IS NOT NULL
                  AND o.created_at > si.at AND (s.session_end IS NULL OR o.created_at < s.session_end)
                  AND NOT EXISTS (SELECT 1 FROM messaging.campaign_recipients cr WHERE cr.message_id = o.id)
                ORDER BY o.created_at, o.id LIMIT 1
            ) fm ON true
            LEFT JOIN LATERAL (
                SELECT min(CASE WHEN i.imported THEN i.occurred_at ELSE i.created_at END) AS at FROM messaging.messages i
                WHERE i.conversation_id = s.conversation_id AND i.direction = 1
            ) fe ON true
            """).ToListAsync(cancellationToken);

        // Un contacto nuevo necesita un entrante en vivo en el periodo; su primer entrante cuenta también el importado, con su occurred_at.
        var newContacts = await dbContext.Database.SqlQuery<NewContactSql>(
            $"""
            SELECT c.id AS "ConversationId", c.channel AS "Channel", f.at AS "FirstInboundAt"
            FROM (
                SELECT DISTINCT m.conversation_id FROM messaging.messages m
                WHERE m.tenant_id = {tenantId} AND m.direction = 1 AND NOT m.imported AND m.created_at >= {start} AND m.created_at < {end}
            ) w
            JOIN messaging.conversations c ON c.id = w.conversation_id
            JOIN LATERAL (
                SELECT min(CASE WHEN i.imported THEN i.occurred_at ELSE i.created_at END) AS at
                FROM messaging.messages i WHERE i.conversation_id = c.id AND i.direction = 1
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
        // Mismas fuentes de sesión que LoadAsync (conversación creada en vivo, Reopened, o el primer entrante en vivo de una importada) y
        // el mismo created_at, sin el filtro de canal.
        var names = await dbContext.Database.SqlQuery<string>(
            $"""
            SELECT c.channel AS "Value" FROM messaging.conversations c
            WHERE c.tenant_id = {tenantId} AND c.created_at >= {startUtc} AND c.created_at < {endExclusiveUtc} AND NOT c.imported AND NOT c.started_by_echo
            UNION
            SELECT c.channel AS "Value" FROM messaging.messages m
            JOIN messaging.conversations c ON c.id = m.conversation_id
            WHERE m.tenant_id = {tenantId} AND m.direction = 3 AND m.kind = 13 AND m.details->>'type' = 'Reopened'
              AND m.created_at >= {startUtc} AND m.created_at < {endExclusiveUtc}
            UNION
            SELECT c.channel AS "Value" FROM messaging.conversations c
            JOIN LATERAL (
                SELECT min(l.created_at) AS at FROM messaging.messages l
                WHERE l.conversation_id = c.id AND l.direction = 1 AND NOT l.imported
            ) fl ON true
            WHERE c.tenant_id = {tenantId} AND c.imported AND fl.at >= {startUtc} AND fl.at < {endExclusiveUtc}
              AND NOT EXISTS (
                  SELECT 1 FROM messaging.messages r
                  WHERE r.conversation_id = c.id AND r.direction = 3 AND r.kind = 13 AND r.details->>'type' = 'Reopened'
                    AND r.created_at <= fl.at)
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
