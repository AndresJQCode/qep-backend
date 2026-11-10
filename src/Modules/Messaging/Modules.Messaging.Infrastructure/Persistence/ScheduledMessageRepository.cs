using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>Spec 2026-11-09: el programado con tracking (la versión es el token de concurrencia). Los guardados pasan por
/// <see cref="MessagingSaveChanges"/>, que traduce los dos únicos de la tabla a <c>scheduled.already_exists</c>.</summary>
internal sealed class ScheduledMessageRepository(MessagingDbContext dbContext) : IScheduledMessageRepository
{
    public Task<ScheduledMessage?> FindByClientIdAsync(Guid tenantId, Guid conversationId, Guid clientId, CancellationToken cancellationToken) =>
        dbContext.ScheduledMessages.AsNoTracking().SingleOrDefaultAsync(
            message => message.TenantId == tenantId && message.ConversationId == conversationId && message.ClientId == clientId, cancellationToken);

    public Task<ScheduledMessage?> FindActiveAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) =>
        dbContext.ScheduledMessages.AsNoTracking().SingleOrDefaultAsync(
            message => message.TenantId == tenantId && message.ConversationId == conversationId &&
                       (message.Status == ScheduledMessageStatus.Scheduled || message.Status == ScheduledMessageStatus.Sending ||
                        message.Status == ScheduledMessageStatus.Failed),
            cancellationToken);

    public async Task<IScheduledMessageLock?> LockAsync(
        Guid tenantId, Guid conversationId, Guid scheduledMessageId, CancellationToken cancellationToken)
    {
        var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // Todas las columnas mapeadas: la fila entra al ChangeTracker y EF escribe sólo lo que el dominio cambió. La lista (y no
            // SELECT *) deja el SQL estable si la tabla gana una columna que el modelo todavía no conoce. Sin LINQ encima: componer sobre
            // FromSql envolvería el FOR UPDATE en una subconsulta.
            var rows = await dbContext.ScheduledMessages.FromSql(
                $"""
                SELECT id, tenant_id, conversation_id, client_id, kind, is_voice_note, text, caption, media_storage_key, media_mime_type,
                       media_file_name, media_size_bytes, media_sha256, scheduled_at, status, author_member_id, created_by_member_id, created_at,
                       updated_by_member_id, updated_at, cancelled_by_member_id, cancelled_at, sent_message_id, failure_code, attempts,
                       locked_until, version
                FROM messaging.scheduled_messages
                WHERE id = {scheduledMessageId} AND tenant_id = {tenantId} AND conversation_id = {conversationId}
                FOR UPDATE
                """).ToListAsync(cancellationToken);
            if (rows.Count == 0)
            {
                await transaction.DisposeAsync();
                return null;
            }

            return new Lock(dbContext, transaction, rows[0]);
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    public async Task AddAsync(ScheduledMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        dbContext.ScheduledMessages.Add(message);
        try
        {
            await MessagingSaveChanges.SaveAsync(dbContext, cancellationToken);
        }
        catch
        {
            // Sin esto el siguiente SaveChanges del mismo request reintentaría el INSERT que ya chocó.
            dbContext.Entry(message).State = EntityState.Detached;
            throw;
        }
    }

    private sealed class Lock(MessagingDbContext dbContext, IDbContextTransaction transaction, ScheduledMessage message) : IScheduledMessageLock
    {
        private bool _closed;

        public ScheduledMessage Message => message;

        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            await MessagingSaveChanges.SaveAsync(dbContext, cancellationToken);
            await CloseAsync(cancellationToken);
        }

        public async Task CommitDiscardAsync(CancellationToken cancellationToken)
        {
            await MessagingSaveChanges.SaveAsync(dbContext, cancellationToken);
            // Precedente: CampaignRepository.SaveCancelledAsync. En la misma transacción, para que el hilo no vea el programado
            // descartado con su mensaje fallido todavía reenviable (el mismo clientId volvería a salir sin que nadie lo pida).
            var failed = MessageColumnCodes.ToCode(MessageStatus.Failed);
            await dbContext.Database.ExecuteSqlAsync(
                $"""
                UPDATE messaging.messages SET retry_disabled = true
                 WHERE conversation_id = {message.ConversationId} AND client_id = {message.ClientId} AND status = {failed}
                """, cancellationToken);
            await CloseAsync(cancellationToken);
        }

        private async Task CloseAsync(CancellationToken cancellationToken)
        {
            // Cerrado antes del commit: si el commit falla, DisposeAsync no intenta un rollback que taparía esa falla.
            _closed = true;
            await transaction.CommitAsync(cancellationToken);
        }

        /// <summary>Desecharlo sin confirmar es rollback. Si ese rollback falla (conexión caída tras un error previo) no se propaga:
        /// taparía la excepción original, y PostgreSQL descarta la transacción igual al cerrarse.</summary>
        public async ValueTask DisposeAsync()
        {
            if (!_closed)
            {
                _closed = true;
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                }
                catch (Exception exception) when (exception is DbException or InvalidOperationException)
                {
                    // Ver summary.
                }
            }

            await transaction.DisposeAsync();
        }
    }
}
