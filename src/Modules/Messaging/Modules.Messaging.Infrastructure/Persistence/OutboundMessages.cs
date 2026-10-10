using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>§8.3: la transacción mantiene un candado sobre <b>una</b> fila de mensaje mientras Meta responde
/// (≤ 10 s); bloquea sólo a otro request con el mismo clientId, nunca la conversación. Soltarla antes de llamar
/// a Meta dejaría ver la fila a medio enviar y permitiría que un segundo request con el mismo clientId
/// la devolviera como enviada (o la reenviara) antes de saber qué contestó Meta.</summary>
internal sealed class OutboundMessages(MessagingDbContext dbContext) : IOutboundMessages
{
    public async Task<IOutboundClaim> ClaimAsync(OutboundDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // Un segundo request con el mismo clientId espera acá hasta que el primero cierre su transacción.
            var inserted = await dbContext.Database.SqlQuery<Guid>(
                $"""
                INSERT INTO messaging.messages (id, conversation_id, tenant_id, connection_id, occurred_at, direction, kind, status, text, client_id, sent_by_member_id, created_at,
                                                reply_to_message_id, reply_to_wamid)
                VALUES ({draft.MessageId}, {draft.ConversationId}, {draft.TenantId}, {draft.ConnectionId}, {draft.Now}, 2, 1, 1, {draft.Text}, {draft.ClientId}, {draft.SentByMemberId}, {draft.Now},
                        {draft.ReplyToMessageId}, {draft.ReplyToWamid})
                ON CONFLICT (conversation_id, client_id) WHERE client_id IS NOT NULL DO NOTHING
                RETURNING id AS "Value"
                """).ToListAsync(cancellationToken);
            if (inserted.Count == 1)
            {
                return new Claim(dbContext, transaction, draft, existing: null);
            }

            // En una sentencia aparte: así ve la fila que el otro request acaba de commitear.
            var rows = await dbContext.Database.SqlQuery<ExistingRow>(
                $"""
                SELECT id AS "Id", status AS "Status", occurred_at AS "OccurredAt", sent_by_member_id AS "SentByMemberId", coalesce(text, '') AS "Text",
                       reply_to_message_id AS "ReplyToMessageId"
                FROM messaging.messages WHERE conversation_id = {draft.ConversationId} AND client_id = {draft.ClientId} FOR UPDATE
                """).ToListAsync(cancellationToken);
            return new Claim(dbContext, transaction, draft, ToExisting(rows.Single()));
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    public async Task<ExistingOutbound?> FindByClientIdAsync(Guid conversationId, Guid clientId, CancellationToken cancellationToken)
    {
        var rows = await dbContext.Database.SqlQuery<ExistingRow>(
            $"""
            SELECT id AS "Id", status AS "Status", occurred_at AS "OccurredAt", sent_by_member_id AS "SentByMemberId",
                   coalesce(text, '') AS "Text", reply_to_message_id AS "ReplyToMessageId"
            FROM messaging.messages WHERE conversation_id = {conversationId} AND client_id = {clientId}
            """).ToListAsync(cancellationToken);
        return rows.Count == 1 ? ToExisting(rows[0]) : null;
    }

    private static ExistingOutbound ToExisting(ExistingRow row) =>
        new(row.Id, MessageColumnCodes.ToStatus(row.Status), row.OccurredAt, row.SentByMemberId, row.Text, row.ReplyToMessageId);

    private sealed class ExistingRow
    {
        public Guid Id { get; set; }

        public short Status { get; set; }

        public DateTimeOffset OccurredAt { get; set; }

        public Guid? SentByMemberId { get; set; }

        public string Text { get; set; } = string.Empty;

        public Guid? ReplyToMessageId { get; set; }
    }

    private sealed class Claim(MessagingDbContext dbContext, IDbContextTransaction transaction, OutboundDraft draft, ExistingOutbound? existing) : IOutboundClaim
    {
        private bool _closed;

        public bool Inserted => existing is null;

        public Guid MessageId => existing?.Id ?? draft.MessageId;

        public ExistingOutbound? Existing => existing;

        public async Task CommitSentAsync(string wamid, DateTimeOffset occurredAt, CancellationToken cancellationToken)
        {
            var preview = draft.Text.Length <= Conversation.PreviewMaxLength ? draft.Text : draft.Text[..Conversation.PreviewMaxLength];
            await dbContext.Database.ExecuteSqlAsync(
                $"""
                UPDATE messaging.messages SET status = 1, wamid = {wamid}, occurred_at = {occurredAt}, text = {draft.Text},
                    failure_code = NULL, failure_title = NULL, sent_by_member_id = {draft.SentByMemberId},
                    reply_to_message_id = {draft.ReplyToMessageId}, reply_to_wamid = {draft.ReplyToWamid}
                WHERE id = {MessageId}
                """, cancellationToken);
            // La foto de la conversación, como la ingesta de §7.5: CASE por fecha para no pisar un entrante más
            // nuevo. No sube version ni updated_at: el envío no es una edición que la persona pueda pisar, y subirla
            // convertiría un resolver justo después de responder en un 412 (mismo criterio que los acuses de §7.5).
            await dbContext.Database.ExecuteSqlAsync(
                $"""
                UPDATE messaging.conversations SET
                    last_activity_at       = GREATEST(last_activity_at, {occurredAt}),
                    last_message_id        = CASE WHEN last_message_at IS NULL OR {occurredAt} >= last_message_at THEN {MessageId} ELSE last_message_id END,
                    last_message_direction = CASE WHEN last_message_at IS NULL OR {occurredAt} >= last_message_at THEN 2 ELSE last_message_direction END,
                    last_message_kind      = CASE WHEN last_message_at IS NULL OR {occurredAt} >= last_message_at THEN 1 ELSE last_message_kind END,
                    last_message_preview   = CASE WHEN last_message_at IS NULL OR {occurredAt} >= last_message_at THEN {preview} ELSE last_message_preview END,
                    last_message_status    = CASE WHEN last_message_at IS NULL OR {occurredAt} >= last_message_at THEN 1 ELSE last_message_status END,
                    last_message_at        = GREATEST(last_message_at, {occurredAt})
                WHERE id = {draft.ConversationId}
                """, cancellationToken);
            // Cerrado antes del commit: si el commit falla, DisposeAsync no intenta un rollback que taparía esa falla.
            _closed = true;
            await transaction.CommitAsync(cancellationToken);
        }

        public async Task CommitFailedAsync(int failureCode, string? failureTitle, DateTimeOffset occurredAt, CancellationToken cancellationToken)
        {
            await dbContext.Database.ExecuteSqlAsync(
                $"""
                UPDATE messaging.messages SET status = 4, failure_code = {failureCode}, failure_title = {failureTitle}, occurred_at = {occurredAt}, text = {draft.Text},
                    reply_to_message_id = {draft.ReplyToMessageId}, reply_to_wamid = {draft.ReplyToWamid}
                WHERE id = {MessageId}
                """, cancellationToken);
            // Cerrado antes del commit: si el commit falla, DisposeAsync no intenta un rollback que taparía esa falla.
            _closed = true;
            await transaction.CommitAsync(cancellationToken);
        }

        public async Task RollbackAsync(CancellationToken cancellationToken)
        {
            if (!_closed)
            {
                _closed = true;
                await transaction.RollbackAsync(cancellationToken);
            }
        }

        /// <summary>Desecharlo sin cerrar es rollback. Si ese rollback falla (conexión caída tras un error previo),
        /// no se propaga: taparía la excepción original, y PostgreSQL descarta la transacción igual al cerrarse.</summary>
        public async ValueTask DisposeAsync()
        {
            try
            {
                await RollbackAsync(CancellationToken.None);
            }
            catch (Exception exception) when (exception is DbException or InvalidOperationException)
            {
                // Ver summary.
            }

            await transaction.DisposeAsync();
        }
    }
}
