using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Domain;
using Modules.Messaging.Infrastructure.Persistence;
using Npgsql;

namespace Modules.Messaging.Infrastructure.Webhook;

/// <summary>Spec 2026-10-10 §8.3, por conexión y en una transacción. Idempotente: si nadie tiene el BSUID anterior, no
/// hay nada que cambiar. Si el nuevo ya tiene conversación (llegó un mensaje antes que la señal), no se fusionan (D-A7):
/// las dos llevan el evento, marcado con la otra (P6) para que una señal repetida no lo duplique.</summary>
internal static class ContactNumberChange
{
    private const string ConnectionUserIndex = "IX_conversations_connection_user";

    public static async Task<IReadOnlyList<Guid>> ApplyAsync(
        MessagingDbContext dbContext, Guid tenantId, Guid connectionId, UserIdChange change, DateTimeOffset occurredAt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            return await ApplyOnceAsync(dbContext, tenantId, connectionId, change, occurredAt, now, cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation && exception.ConstraintName == ConnectionUserIndex)
        {
            // Un mensaje con el BSUID nuevo creó su conversación entre el SELECT y el UPDATE: la segunda vuelta ve el choque.
            return await ApplyOnceAsync(dbContext, tenantId, connectionId, change, occurredAt, now, cancellationToken);
        }
    }

    private static async Task<IReadOnlyList<Guid>> ApplyOnceAsync(
        MessagingDbContext dbContext, Guid tenantId, Guid connectionId, UserIdChange change, DateTimeOffset occurredAt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var source = await IdByUserAsync(dbContext, connectionId, change.Previous, cancellationToken);
        if (source is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return [];
        }

        var target = await IdByUserAsync(dbContext, connectionId, change.Current, cancellationToken);
        if (target is null)
        {
            await dbContext.Database.ExecuteSqlAsync(
                $"""
                UPDATE messaging.conversations
                   SET user_id = {change.Current}, wa_id = COALESCE({change.WaId}, wa_id), version = version + 1, updated_at = {now}
                 WHERE id = {source.Value}
                """, cancellationToken);
            await ConversationEventRows.InsertAsync(dbContext, tenantId, connectionId, source.Value,
                new ConversationEvent(ConversationEventType.ContactChangedNumber), occurredAt, now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return [source.Value];
        }

        var already = await dbContext.Database.SqlQuery<int>(
            $"""
            SELECT 1 AS "Value" FROM messaging.messages
            WHERE conversation_id = {target.Value} AND direction = 3
              AND details->>'type' = 'ContactChangedNumber' AND details->>'linkedConversationId' = {source.Value.ToString("D")}
            """).ToListAsync(cancellationToken);
        if (already.Count == 0)
        {
            await ConversationEventRows.InsertAsync(dbContext, tenantId, connectionId, source.Value,
                new ConversationEvent(ConversationEventType.ContactChangedNumber, LinkedConversationId: target.Value), occurredAt, now, cancellationToken);
            await ConversationEventRows.InsertAsync(dbContext, tenantId, connectionId, target.Value,
                new ConversationEvent(ConversationEventType.ContactChangedNumber, LinkedConversationId: source.Value), occurredAt, now, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return already.Count == 0 ? [source.Value, target.Value] : [];
    }

    private static async Task<Guid?> IdByUserAsync(MessagingDbContext dbContext, Guid connectionId, string userId, CancellationToken cancellationToken)
    {
        var rows = await dbContext.Database.SqlQuery<Guid>(
            $"""SELECT id AS "Value" FROM messaging.conversations WHERE connection_id = {connectionId} AND user_id = {userId} FOR UPDATE""")
            .ToListAsync(cancellationToken);
        return rows.Count == 1 ? rows[0] : null;
    }
}
