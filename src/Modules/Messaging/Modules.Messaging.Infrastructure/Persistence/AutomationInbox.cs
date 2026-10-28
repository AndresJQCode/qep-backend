using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>Spec 2026-10-21 §5.1: SQL a mano, con la forma del reclamo de <c>MediaCopyWorker</c> (UPDATE … FROM (SELECT … FOR UPDATE
/// SKIP LOCKED)); un reclamo se commitea solo, así que dos réplicas no toman la misma fila.</summary>
internal sealed class AutomationInbox(MessagingDbContext dbContext, IClock clock) : IAutomationInbox
{
    /// <summary>Tras este número de intentos fallidos la fila se rinde.</summary>
    internal const int MaxAttempts = 5;

    /// <summary>El lease del intento n es <c>Leases[n - 1]</c>, y desde el último, el último. Es también el tiempo que tiene el motor
    /// para terminar antes de que otra réplica retome la fila.</summary>
    internal static readonly TimeSpan[] Leases = [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10)];

    public async Task<IReadOnlyList<AutomationInboxItem>> ClaimAsync(DateTimeOffset now, int batch, CancellationToken cancellationToken)
    {
        var leases = Leases;
        // Una fila que ya gastó sus intentos y cuyo lease venció es un worker que se cayó o se colgó sin llamar a RetryLaterAsync:
        // se cierra acá, o se reclamaría para siempre y frenaría su conversación. Con el lease vigente todavía puede estar en curso.
        await dbContext.Database.ExecuteSqlAsync(
            $"""
            UPDATE messaging.automation_inbox SET processed_at = {now}, outcome = 'failed'
             WHERE processed_at IS NULL AND attempts >= {MaxAttempts} AND next_attempt_at <= {now}
            """, cancellationToken);
        // El NOT EXISTS mira también las filas en lease: una pendiente más vieja de la misma conversación frena a las demás hasta que se
        // cierre. created_at es distinto y creciente por mensaje (el procesador del webhook suma un microsegundo por posición); message_id sólo desempata filas de entregas distintas con el mismo instante.
        return await dbContext.Database.SqlQuery<AutomationInboxItem>(
            $"""
            UPDATE messaging.automation_inbox AS inbox
               SET next_attempt_at = {now} + ({leases})[LEAST(inbox.attempts + 1, cardinality({leases}))],
                   attempts = inbox.attempts + 1
              FROM (SELECT i.message_id FROM messaging.automation_inbox AS i
                     WHERE i.processed_at IS NULL AND i.next_attempt_at <= {now}
                       AND NOT EXISTS (SELECT 1 FROM messaging.automation_inbox AS o
                                        WHERE o.conversation_id = i.conversation_id AND o.processed_at IS NULL
                                          AND (o.created_at, o.message_id) < (i.created_at, i.message_id))
                     ORDER BY i.created_at, i.message_id LIMIT {batch} FOR UPDATE SKIP LOCKED) AS pending
             WHERE inbox.message_id = pending.message_id
            RETURNING inbox.message_id AS "MessageId", inbox.tenant_id AS "TenantId", inbox.connection_id AS "ConnectionId",
                      inbox.conversation_id AS "ConversationId", inbox.opened AS "Opened", inbox.created_at AS "CreatedAt",
                      inbox.attempts::int AS "Attempts"
            """).ToListAsync(cancellationToken);
    }

    // El filtro por attempts es la cerca contra un lease vencido (ver IAutomationInbox); el de tenant, la doble verificación de siempre.
    public async Task<bool> CompleteAsync(Guid tenantId, Guid messageId, int attempts, string outcome, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        return await dbContext.Database.ExecuteSqlAsync(
            $"""
            UPDATE messaging.automation_inbox SET processed_at = {now}, outcome = {outcome}
             WHERE message_id = {messageId} AND tenant_id = {tenantId} AND attempts = {attempts} AND processed_at IS NULL
            """,
            cancellationToken) > 0;
    }

    public async Task<bool> RetryLaterAsync(Guid tenantId, Guid messageId, int attempts, TimeSpan delay, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var retryAt = now + delay;
        return await dbContext.Database.ExecuteSqlAsync(
            $"""
            UPDATE messaging.automation_inbox
               SET next_attempt_at = {retryAt},
                   processed_at = CASE WHEN attempts >= {MaxAttempts} THEN {now} ELSE NULL END,
                   outcome = CASE WHEN attempts >= {MaxAttempts} THEN 'failed' ELSE NULL END
             WHERE message_id = {messageId} AND tenant_id = {tenantId} AND attempts = {attempts} AND processed_at IS NULL
            """, cancellationToken) > 0;
    }

    public async Task<InboundForAutomation?> ReadInboundAsync(Guid tenantId, Guid messageId, CancellationToken cancellationToken)
    {
        // details guarda el objeto `interactive` de Meta tal cual (WebhookPayloadParser): button_reply / list_reply cuelgan de la raíz.
        var rows = await dbContext.Database.SqlQuery<InboundRow>(
            $"""
            SELECT message.id AS "MessageId", message.kind AS "Kind", COALESCE(message.text, message.caption) AS "Text",
                   COALESCE(message.details->'button_reply'->>'id', message.details->'list_reply'->>'id') AS "ReplyId",
                   media.mime_type AS "MimeType", media.file_name AS "FileName", (media.stored_at IS NOT NULL) AS "Stored",
                   message.occurred_at AS "OccurredAt", message.wamid AS "Wamid", message.reply_to_message_id AS "ReplyToMessageId",
                   message.conversation_id AS "ConversationId"
              FROM messaging.messages AS message
              LEFT JOIN messaging.message_media AS media ON media.message_id = message.id
             WHERE message.id = {messageId} AND message.tenant_id = {tenantId} AND message.direction = 1
            """).ToListAsync(cancellationToken);
        return rows.Count == 0 ? null : rows[0].ToInbound();
    }

    public async Task<Guid?> FindLastInboundIdAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken)
    {
        // Por IX_messages_thread (conversation_id, occurred_at, id), el mismo orden del hilo.
        var rows = await dbContext.Database.SqlQuery<Guid>(
            $"""
            SELECT message.id AS "Value" FROM messaging.messages AS message
             WHERE message.conversation_id = {conversationId} AND message.tenant_id = {tenantId} AND message.direction = 1
             ORDER BY message.occurred_at DESC, message.id DESC
             LIMIT 1
            """).ToListAsync(cancellationToken);
        return rows.Count == 0 ? null : rows[0];
    }

    // Por la llave primaria, como la purga de webhook_deliveries (WebhookPurgeWorker.cs:60-64). Sin índice sobre processed_at a propósito:
    // los dos que hay son parciales sobre las pendientes, y la tabla sólo guarda la ventana de retención.
    public async Task<int> PurgeProcessedBeforeAsync(DateTimeOffset cutoff, int batch, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batch);
        // Npgsql sólo escribe un timestamptz desde un DateTimeOffset en UTC.
        cutoff = cutoff.ToUniversalTime();
        return await dbContext.Database.ExecuteSqlAsync(
            $"""
            DELETE FROM messaging.automation_inbox
             WHERE message_id IN (SELECT message_id FROM messaging.automation_inbox
                                   WHERE processed_at IS NOT NULL AND processed_at < {cutoff} LIMIT {batch})
            """, cancellationToken);
    }

    private sealed record InboundRow(
        Guid MessageId, short Kind, string? Text, string? ReplyId, string? MimeType, string? FileName, bool Stored, DateTimeOffset OccurredAt, string? Wamid,
        Guid? ReplyToMessageId, Guid ConversationId)
    {
        public InboundForAutomation ToInbound() => new(
            MessageId,
            MessageColumnCodes.ToKind(Kind),
            Text,
            ReplyId,
            MimeType is null ? null : new InboundMediaForAutomation(MimeType, FileName, Stored),
            OccurredAt,
            Wamid,
            ReplyToMessageId,
            ConversationId);
    }
}
