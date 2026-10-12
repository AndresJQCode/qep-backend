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
        // Meta casi siempre manda errors[] con el failed; 0 es «falló sin código» y cae en el failureReason genérico.
        int? failureCode = update.Status == MessageStatus.Failed ? update.ErrorCode ?? 0 : null;
        var failureTitle = update.Status == MessageStatus.Failed ? update.ErrorTitle : null;

        // §8.2: cada cambio en su transacción. El candado que toma el UPDATE del mensaje se mantiene hasta que la
        // foto commitea: dos réplicas con el delivered y el read del mismo mensaje se serializan, y la foto no
        // puede quedar en el estado del que perdió. Además el par es atómico: un corte entre los dos no deja
        // la foto vieja para siempre (el reintento vería NoChange y no la repararía).
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
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
            // RETURNING vacío no distingue «no existe» de «existe pero no avanza»; el reintento depende de eso.
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

        await transaction.CommitAsync(cancellationToken);
        return StatusOutcome.Applied;
    }

    /// <summary>Spec 2026-10-11 decisión 10: Meta aceptó una plantilla de marketing y la reporta después con este código, porque la persona
    /// apagó las promociones de la organización.</summary>
    public const int MarketingOptOutCode = 131050;

    /// <summary>El cliente de la conversación del mensaje que falló con <see cref="MarketingOptOutCode"/>; <c>null</c> si el estado es otro, el
    /// mensaje no es nuestro o la conversación no tiene cliente. Se consulta aparte de <see cref="ApplyAsync"/> y también cuando el estado ya
    /// estaba aplicado (<see cref="StatusOutcome.NoChange"/>): si el aviso al cliente falló, el reintento de la entrega todavía lo marca.</summary>
    public static async Task<Guid?> FindMarketingOptOutCustomerAsync(
        MessagingDbContext dbContext, Guid connectionId, StatusUpdate update, CancellationToken cancellationToken)
    {
        if (update.Status != MessageStatus.Failed || update.ErrorCode != MarketingOptOutCode)
        {
            return null;
        }

        var callback = update.CallbackMessageId ?? Guid.Empty;
        var customers = await dbContext.Database.SqlQuery<Guid>(
            $"""
            SELECT c.customer_id AS "Value"
            FROM messaging.messages m JOIN messaging.conversations c ON c.id = m.conversation_id
            WHERE m.connection_id = {connectionId} AND (m.wamid = {update.Wamid} OR m.id = {callback}) AND m.direction = 2 AND c.customer_id IS NOT NULL
            """).ToListAsync(cancellationToken);
        return customers.Count == 1 ? customers[0] : null;
    }

    private sealed record AppliedRow(Guid Id, Guid ConversationId);
}
