using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Domain;
using Modules.Messaging.Infrastructure.Persistence;

namespace Modules.Messaging.Infrastructure.Webhook;

internal enum StatusOutcome
{
    Applied,
    NoChange,
    NotFound,
}

/// <summary>Spec 2026-10-09 §7.5 («Estados»), literal: un UPDATE monótono por índice único. Si tocó el último
/// de su conversación, actualiza la foto sin subir version ni updated_at (HOT).</summary>
internal static class StatusIngestion
{
    public static async Task<StatusOutcome> ApplyAsync(MessagingDbContext dbContext, Guid connectionId, StatusUpdate update, CancellationToken cancellationToken)
    {
        var newStatus = MessageColumnCodes.ToCode(update.Status);
        // Sin callback no hay id que buscar: Guid.Empty no coincide con ninguna fila.
        var callback = update.CallbackMessageId ?? Guid.Empty;
        int? failureCode = update.Status == MessageStatus.Failed ? update.ErrorCode ?? 0 : null;
        var failureTitle = update.Status == MessageStatus.Failed ? update.ErrorTitle : null;
        var updated = await dbContext.Database.SqlQuery<AppliedRow>(
            $"""
            UPDATE messaging.messages
            SET status = {newStatus}, failure_code = {failureCode}, failure_title = {failureTitle},
                wamid = COALESCE(wamid, {update.Wamid})
            WHERE connection_id = {connectionId}
              AND (wamid = {update.Wamid} OR id = {callback})
              AND direction = 2
              AND (
                    ({newStatus} IN (2, 3) AND (status < {newStatus} OR (status = 4 AND failure_code = -1)))
                 OR ({newStatus} = 1       AND status = 4 AND failure_code = -1)
                 OR ({newStatus} = 4       AND status = 1)
              )
            RETURNING id AS "Id", conversation_id AS "ConversationId"
            """).ToListAsync(cancellationToken);

        if (updated.Count == 0)
        {
            var exists = await dbContext.Database.SqlQuery<int>(
                $"""SELECT 1 AS "Value" FROM messaging.messages WHERE connection_id = {connectionId} AND (wamid = {update.Wamid} OR id = {callback}) AND direction = 2""")
                .ToListAsync(cancellationToken);
            return exists.Count == 0 ? StatusOutcome.NotFound : StatusOutcome.NoChange;
        }

        foreach (var row in updated)
        {
            // §7.5: sin subir version ni updated_at; un acuse no puede convertir un resolve en 412.
            await dbContext.Database.ExecuteSqlAsync(
                $"UPDATE messaging.conversations SET last_message_status = {newStatus} WHERE id = {row.ConversationId} AND last_message_id = {row.Id}",
                cancellationToken);
        }

        return StatusOutcome.Applied;
    }

    private sealed record AppliedRow(Guid Id, Guid ConversationId);
}
