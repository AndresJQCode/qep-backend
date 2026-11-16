using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Modules.Messaging.Domain;
using Modules.Support.Application;
using Modules.Support.Infrastructure;
using Modules.Support.Infrastructure.Persistence;

namespace Bootstrapper;

/// <summary>
/// Fase 4b, Tarea 12 (H18, owner 2026-11-16, Q-11): lo que el worker de la encuesta lee de las respuestas por WhatsApp, de todos los
/// tenants, por SQL crudo sobre la conexión de <see cref="SupportDbContext"/> (Mensajería no avisa, como en <see cref="SupportThreadFeed"/>).
///
/// **Toques.** Un toque es un entrante en vivo (<c>direction</c> entrante, no <c>imported</c>) de tipo <c>Interactive</c> cuyo
/// <c>reply_to_message_id</c> es el <c>message_id</c> de una encuesta mandada por WhatsApp y sin calificar (Meta lo cita con
/// <c>context.id</c> y la ingesta lo resuelve, V-1). Sólo de encuestas que todavía se responden (H21: el ticket Resuelto o Cerrado y en esa
/// misma resolución), así un toque sobre una encuesta vieja no ocupa el lote en cada tick. Se recorre la conversación del ticket por
/// <c>IX_messages_thread</c> (<c>conversation_id, occurred_at</c>) desde que se pidió la encuesta, y sólo encuestas mandadas en las
/// últimas <c>Support:Workers:SurveyAnswerLookbackDays</c> (14).
///
/// **Comentarios.** Por encuesta calificada por WhatsApp y sin comentario: el primer entrante en vivo de tipo <c>Text</c> de la
/// conversación del toque, escrito después del toque (por <c>occurred_at</c> y el id de desempate) y antes de <c>comment_until</c>. Una
/// ventana ya cerrada se sigue mirando durante el lookback: el texto que llegó a tiempo cuenta aunque el worker vaya tarde.
///
/// **Lo que lee por nombre.** <c>support.ticket_surveys</c> (<c>id</c>, <c>tenant_id</c>, <c>ticket_id</c>, <c>resolution_number</c>,
/// <c>status</c>, <c>channel</c>, <c>message_id</c>, <c>sent_at</c>, <c>created_at</c>, <c>rating</c>, <c>answered_via</c>,
/// <c>answered_at</c>, <c>rating_message_id</c>, <c>comment</c>, <c>comment_until</c>), <c>support.tickets</c> (<c>id</c>,
/// <c>tenant_id</c>, <c>conversation_id</c>, <c>status</c>, <c>resolution_count</c>) y <c>messaging.messages</c> (<c>id</c>,
/// <c>tenant_id</c>, <c>conversation_id</c>, <c>direction</c>, <c>kind</c>, <c>text</c>, <c>reply_to_message_id</c>,
/// <c>occurred_at</c>, <c>imported</c>). Renombrar una compila y deja la encuesta sin respuestas, sin error; lo amarra
/// <c>SupportBootstrapperAdaptersTests.TheSurveyFeedRunsAgainstTheMigratedSchema</c>.
/// </summary>
internal sealed class SupportSurveyFeed(SupportDbContext support, IOptions<SupportWorkerOptions> options) : ISupportSurveyFeed
{
    private const short InboundDirection = (short)MessageDirection.Inbound;

    private const short InteractiveKind = (short)MessageKind.Interactive;

    private const short TextKind = (short)MessageKind.Text;

    /// <summary>Cuántos toques por encuesta se leen: el primero cuyo texto es un botón gana, y uno con otro texto no tapa los demás.</summary>
    private const int TapsPerSurvey = 5;

    private TimeSpan Lookback => TimeSpan.FromDays(Math.Max(1, options.Value.SurveyAnswerLookbackDays));

    public async Task<IReadOnlyList<SupportSurveyTap>> ListTapsAsync(int take, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);
        var lookback = Lookback;
        var rows = await support.Database.SqlQuery<TapRow>($"""
            SELECT s.tenant_id AS "TenantId", s.id AS "SurveyId", m.id AS "MessageId", m.text AS "Text", m.occurred_at AS "OccurredAt"
            FROM support.ticket_surveys s
            JOIN support.tickets t ON t.id = s.ticket_id AND t.tenant_id = s.tenant_id
            JOIN LATERAL (
                SELECT m.id, m.text, m.occurred_at
                FROM messaging.messages m
                WHERE m.tenant_id = s.tenant_id
                  AND m.conversation_id = t.conversation_id
                  AND m.occurred_at >= s.created_at
                  AND m.reply_to_message_id = s.message_id
                  AND m.direction = {InboundDirection}
                  AND m.kind = {InteractiveKind}
                  AND NOT m.imported
                ORDER BY m.occurred_at, m.id
                LIMIT {TapsPerSurvey}) m ON true
            WHERE s.status = 'Sent'
              AND s.channel = 'WhatsApp'
              AND s.rating IS NULL
              AND s.message_id IS NOT NULL
              AND s.sent_at >= now() - {lookback}
              AND t.status IN ('Resolved', 'Closed')
              AND t.resolution_count = s.resolution_number
            ORDER BY s.sent_at, s.id, m.occurred_at, m.id
            LIMIT {take}
            """).ToListAsync(cancellationToken);
        return rows.Select(row => new SupportSurveyTap(row.TenantId, row.SurveyId, row.MessageId, row.Text, row.OccurredAt)).ToList();
    }

    public async Task<IReadOnlyList<SupportSurveyComment>> ListCommentsAsync(int take, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);
        var lookback = Lookback;
        var rows = await support.Database.SqlQuery<CommentRow>($"""
            SELECT s.tenant_id AS "TenantId", s.id AS "SurveyId", m.id AS "MessageId", m.text AS "Text", m.occurred_at AS "OccurredAt"
            FROM support.ticket_surveys s
            JOIN messaging.messages tap ON tap.id = s.rating_message_id AND tap.tenant_id = s.tenant_id
            JOIN LATERAL (
                SELECT m.id, m.text, m.occurred_at
                FROM messaging.messages m
                WHERE m.tenant_id = s.tenant_id
                  AND m.conversation_id = tap.conversation_id
                  AND (m.occurred_at, m.id) > (tap.occurred_at, tap.id)
                  AND m.occurred_at < s.comment_until
                  AND m.direction = {InboundDirection}
                  AND m.kind = {TextKind}
                  AND NOT m.imported
                  AND m.text IS NOT NULL
                  AND btrim(m.text) <> ''
                ORDER BY m.occurred_at, m.id
                LIMIT 1) m ON true
            WHERE s.rating IS NOT NULL
              AND s.answered_via = 'WhatsApp'
              AND s.comment IS NULL
              AND s.comment_until > now() - {lookback}
            ORDER BY s.answered_at, s.id
            LIMIT {take}
            """).ToListAsync(cancellationToken);
        return rows.Select(row => new SupportSurveyComment(row.TenantId, row.SurveyId, row.MessageId, row.Text, row.OccurredAt)).ToList();
    }

    private sealed class TapRow
    {
        public Guid TenantId { get; init; }

        public Guid SurveyId { get; init; }

        public Guid MessageId { get; init; }

        public string? Text { get; init; }

        public DateTimeOffset OccurredAt { get; init; }
    }

    private sealed class CommentRow
    {
        public Guid TenantId { get; init; }

        public Guid SurveyId { get; init; }

        public Guid MessageId { get; init; }

        public string Text { get; init; } = string.Empty;

        public DateTimeOffset OccurredAt { get; init; }
    }
}
