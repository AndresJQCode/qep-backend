using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using Modules.Messaging.Infrastructure.Persistence;

namespace Modules.Messaging.Infrastructure.ScheduledMessages;

/// <summary>
/// Spec 2026-11-09 «Worker y ejecución»: el SQL del worker de programados, calco de <c>CampaignDispatchStore</c>. Cross-tenant y sin
/// tracking. Los dos reclamos son una sola sentencia con <c>FOR UPDATE SKIP LOCKED</c>: dos pods nunca toman la misma fila, y una fila que
/// una edición tiene con <c>FOR UPDATE</c> (<c>ScheduledMessageRepository.LockAsync</c>) se salta en ese tick. Los cierres son
/// compare-and-set por <c>(id, status = 'Sending', attempts)</c> y escriben la auditoría en la misma transacción.
/// </summary>
internal sealed class ScheduledMessageStore(MessagingDbContext dbContext, IMessagingAuditRecorder audit) : IScheduledMessageStore
{
    public async Task<IReadOnlyList<ClaimedScheduledMessage>> ClaimDueAsync(
        int take, DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken)
    {
        // El status del UPDATE externo repite la guarda: si otro pod confirmó su reclamo entre la subconsulta y el candado, Postgres
        // reevalúa la fila nueva y la deja fuera (con SKIP LOCKED, perder da menos filas, nunca repetidas).
        var rows = await dbContext.Database.SqlQuery<ClaimRow>(
            $"""
            UPDATE messaging.scheduled_messages
               SET status = 'Sending', locked_until = {now} + {lease}, attempts = attempts + 1, version = version + 1
             WHERE id IN (SELECT id FROM messaging.scheduled_messages
                           WHERE status = 'Scheduled' AND scheduled_at <= {now}
                           ORDER BY scheduled_at LIMIT {take} FOR UPDATE SKIP LOCKED)
               AND status = 'Scheduled'
            RETURNING id AS "Id", tenant_id AS "TenantId", conversation_id AS "ConversationId", client_id AS "ClientId", kind AS "Kind",
                      is_voice_note AS "IsVoiceNote", text AS "Text", caption AS "Caption", media_storage_key AS "MediaStorageKey",
                      media_mime_type AS "MediaMimeType", media_file_name AS "MediaFileName", media_size_bytes AS "MediaSizeBytes",
                      media_sha256 AS "MediaSha256", author_member_id AS "AuthorMemberId", attempts AS "Attempts"
            """).ToListAsync(cancellationToken);
        return [.. rows.Select(ToClaimed)];
    }

    public async Task<IReadOnlyList<ClaimedScheduledMessage>> ClaimExpiredAsync(
        int take, DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken)
    {
        // D9: sólo renueva el lease. Sin sumar intentos ni versión: el cierre de la recuperación es el CAS con el intento que ya tenía, así
        // que si el worker original termina tarde, su cierre (mismo intento) compite con el de la recuperación y gana uno solo.
        var rows = await dbContext.Database.SqlQuery<ClaimRow>(
            $"""
            UPDATE messaging.scheduled_messages
               SET locked_until = {now} + {lease}
             WHERE id IN (SELECT id FROM messaging.scheduled_messages
                           WHERE status = 'Sending' AND locked_until < {now}
                           ORDER BY locked_until LIMIT {take} FOR UPDATE SKIP LOCKED)
               AND status = 'Sending' AND locked_until < {now}
            RETURNING id AS "Id", tenant_id AS "TenantId", conversation_id AS "ConversationId", client_id AS "ClientId", kind AS "Kind",
                      is_voice_note AS "IsVoiceNote", text AS "Text", caption AS "Caption", media_storage_key AS "MediaStorageKey",
                      media_mime_type AS "MediaMimeType", media_file_name AS "MediaFileName", media_size_bytes AS "MediaSizeBytes",
                      media_sha256 AS "MediaSha256", author_member_id AS "AuthorMemberId", attempts AS "Attempts"
            """).ToListAsync(cancellationToken);
        return [.. rows.Select(ToClaimed)];
    }

    public async Task<bool> CompleteSentAsync(
        ClaimedScheduledMessage message, Guid sentMessageId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var updated = await dbContext.Database.ExecuteSqlAsync(
            $"""
            UPDATE messaging.scheduled_messages
               SET status = 'Sent', sent_message_id = {sentMessageId}, failure_code = NULL, locked_until = NULL, version = version + 1
             WHERE id = {message.Id} AND status = 'Sending' AND attempts = {message.Attempts}
            """, cancellationToken);
        return await FinishAsync(transaction, updated, message, MessagingAuditActions.ScheduledMessageSent, now, cancellationToken);
    }

    public async Task<bool> CompleteFailedAsync(
        ClaimedScheduledMessage message, string failureCode, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var updated = await dbContext.Database.ExecuteSqlAsync(
            $"""
            UPDATE messaging.scheduled_messages
               SET status = 'Failed', failure_code = {failureCode}, locked_until = NULL, version = version + 1
             WHERE id = {message.Id} AND status = 'Sending' AND attempts = {message.Attempts}
            """, cancellationToken);
        return await FinishAsync(transaction, updated, message, MessagingAuditActions.ScheduledMessageFailed, now, cancellationToken);
    }

    public async Task<IReadOnlyList<PurgeableScheduledMedia>> ListPurgeableAsync(int take, CancellationToken cancellationToken)
    {
        // Sólo terminales: Failed sigue siendo reintentable y necesita su objeto.
        var rows = await dbContext.Database.SqlQuery<PurgeRow>(
            $"""
            SELECT tenant_id AS "TenantId", id AS "Id", media_storage_key AS "StorageKey"
              FROM messaging.scheduled_messages
             WHERE status IN ('Sent', 'Cancelled') AND media_storage_key IS NOT NULL
             ORDER BY id
             LIMIT {take}
            """).ToListAsync(cancellationToken);
        return [.. rows.Select(row => new PurgeableScheduledMedia(row.TenantId, row.Id, row.StorageKey))];
    }

    public async Task ClearMediaKeyAsync(Guid tenantId, Guid id, CancellationToken cancellationToken) =>
        // La misma guarda que la lista: la clave de una fila activa nunca se borra, aunque alguien llame con su id.
        await dbContext.Database.ExecuteSqlAsync(
            $"""
            UPDATE messaging.scheduled_messages SET media_storage_key = NULL
             WHERE id = {id} AND tenant_id = {tenantId} AND status IN ('Sent', 'Cancelled')
            """, cancellationToken);

    /// <summary>Si el CAS tocó la fila, la auditoría <c>System</c> va en la misma transacción; si no, no se escribe nada.</summary>
    private async Task<bool> FinishAsync(
        IDbContextTransaction transaction, int updated, ClaimedScheduledMessage message, string action,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (updated != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        audit.RecordScheduledMessage(message.TenantId, Guid.Empty, bySystem: true, action, message.Id, now);
        await MessagingSaveChanges.SaveAsync(dbContext, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static ClaimedScheduledMessage ToClaimed(ClaimRow row) => new(
        row.Id, row.TenantId, row.ConversationId, row.ClientId, Enum.Parse<MessageKind>(row.Kind), row.IsVoiceNote, row.Text, row.Caption,
        row.MediaStorageKey, row.MediaMimeType, row.MediaFileName, row.MediaSizeBytes, row.MediaSha256, row.AuthorMemberId, row.Attempts);

    private sealed class ClaimRow
    {
        public Guid Id { get; set; }

        public Guid TenantId { get; set; }

        public Guid ConversationId { get; set; }

        public Guid ClientId { get; set; }

        public string Kind { get; set; } = string.Empty;

        public bool IsVoiceNote { get; set; }

        public string? Text { get; set; }

        public string? Caption { get; set; }

        public string? MediaStorageKey { get; set; }

        public string? MediaMimeType { get; set; }

        public string? MediaFileName { get; set; }

        public long? MediaSizeBytes { get; set; }

        public string? MediaSha256 { get; set; }

        public Guid AuthorMemberId { get; set; }

        public int Attempts { get; set; }
    }

    private sealed class PurgeRow
    {
        public Guid TenantId { get; set; }

        public Guid Id { get; set; }

        public string StorageKey { get; set; } = string.Empty;
    }
}
