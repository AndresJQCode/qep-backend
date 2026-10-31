using System.Data;
using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Infrastructure.Persistence;

namespace Bootstrapper.Observability;

/// <summary>
/// Observabilidad fase 2: lee la profundidad y la edad de las seis colas con SQL crudo sobre la conexión de
/// <see cref="MessagingDbContext"/> (precedente <c>PipelineConversationDirectory</c>). Conoce por nombre <c>messaging.webhook_deliveries</c>,
/// <c>automation_inbox</c>, <c>message_media</c>, <c>messages</c>, <c>campaign_recipients</c> y <c>campaigns</c>, <c>platform.outbox_messages</c> y
/// <c>shopify.webhook_deliveries</c>: renombrar una columna compila y deja esa cola sin dato. Lo amarra <c>QueueMetricsSourceTests</c>.
///
/// La edad mira sólo filas con <c>attempts = 0</c>: una sana en reintento largo o un outbox envenenado no son atraso. En <c>automation</c>
/// además salta las frenadas por una anterior pendiente de su conversación (el mismo <c>NOT EXISTS</c> de <c>AutomationInbox.ClaimAsync</c>).
/// Cada consulta lleva tope de 5 s.
/// </summary>
internal sealed class QueueMetricsSource(MessagingDbContext messaging) : IQueueMetricsSource
{
    private const int CommandTimeoutSeconds = 5;

    // Forma común: backlog, reintentando, edad en segundos (0 si no hay filas que cuenten).
    private static readonly Dictionary<string, string> Queries = new(StringComparer.Ordinal)
    {
        [QueueNames.Webhook] = """
            SELECT count(*), count(*) FILTER (WHERE attempts > 0),
                   COALESCE(EXTRACT(EPOCH FROM now() - min(received_at) FILTER (WHERE attempts = 0))::float8, 0)
              FROM messaging.webhook_deliveries WHERE processed_at IS NULL
            """,
        [QueueNames.Automation] = """
            SELECT count(*), count(*) FILTER (WHERE i.attempts > 0),
                   COALESCE(EXTRACT(EPOCH FROM now() - min(i.created_at) FILTER (WHERE i.attempts = 0
                       AND NOT EXISTS (SELECT 1 FROM messaging.automation_inbox AS o
                                        WHERE o.conversation_id = i.conversation_id AND o.processed_at IS NULL
                                          AND (o.created_at, o.message_id) < (i.created_at, i.message_id))))::float8, 0)
              FROM messaging.automation_inbox AS i WHERE i.processed_at IS NULL
            """,
        // imported saca al historial de la edad (sus medios nacen viejos), no del backlog. next_attempt_at = 9999-01-01 es un medio abandonado.
        [QueueNames.Media] = """
            SELECT count(*), count(*) FILTER (WHERE mm.attempts > 0),
                   COALESCE(EXTRACT(EPOCH FROM now() - min(m.created_at) FILTER (WHERE mm.attempts = 0 AND NOT m.imported))::float8, 0)
              FROM messaging.message_media AS mm
              JOIN messaging.messages AS m ON m.id = mm.message_id
             WHERE mm.stored_at IS NULL AND mm.next_attempt_at < '9999-01-01+00'
            """,
        // Sin columna de edad: la tercera es NULL y el gauge no publica.
        [QueueNames.Campaign] = """
            SELECT count(*), count(*) FILTER (WHERE r.attempts > 0), NULL::float8
              FROM messaging.campaign_recipients AS r
              JOIN messaging.campaigns AS c ON c.id = r.campaign_id
             WHERE r.status IN ('Pending', 'Sending') AND c.status = 'Sending' AND c.audience_frozen_at IS NOT NULL
            """,
        [QueueNames.Outbox] = """
            SELECT count(*), count(*) FILTER (WHERE attempts > 0),
                   COALESCE(EXTRACT(EPOCH FROM now() - min(occurred_at) FILTER (WHERE attempts = 0))::float8, 0)
              FROM platform.outbox_messages WHERE processed_at IS NULL
            """,
        [QueueNames.ShopifyWebhook] = """
            SELECT count(*), count(*) FILTER (WHERE attempts > 0),
                   COALESCE(EXTRACT(EPOCH FROM now() - min(received_at) FILTER (WHERE attempts = 0))::float8, 0)
              FROM shopify.webhook_deliveries WHERE processed_at IS NULL
            """,
    };

    public async Task<QueueReading> ReadAsync(string queue, CancellationToken cancellationToken)
    {
        if (queue is null || !Queries.TryGetValue(queue, out var sql))
        {
            throw new ArgumentOutOfRangeException(nameof(queue), queue, "Cola desconocida.");
        }

        var connection = messaging.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = CommandTimeoutSeconds;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            return new QueueReading(
                queue,
                reader.GetInt64(0),
                reader.GetInt64(1),
                await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetDouble(2));
        }
        finally
        {
            if (opened)
            {
                await connection.CloseAsync();
            }
        }
    }
}
