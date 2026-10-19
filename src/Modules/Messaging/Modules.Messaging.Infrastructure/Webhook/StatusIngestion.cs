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

/// <summary>El resultado y, con <see cref="StatusOutcome.Applied"/>, las conversaciones cuya foto cambió: el aviso en tiempo real
/// las nombra (spec 2026-10-14).</summary>
internal sealed record StatusApplied(StatusOutcome Outcome, IReadOnlyList<Guid> ConversationIds)
{
    public static readonly StatusApplied NotFound = new(StatusOutcome.NotFound, []);

    public static readonly StatusApplied NoChange = new(StatusOutcome.NoChange, []);
}

/// <summary>Spec 2026-10-09 §7.5 («Estados»), literal: un UPDATE monótono por índice único. Si tocó el último
/// de su conversación, actualiza la foto sin subir version ni updated_at (HOT).</summary>
internal static class StatusIngestion
{
    public static async Task<StatusApplied> ApplyAsync(MessagingDbContext dbContext, Guid connectionId, StatusUpdate update, CancellationToken cancellationToken)
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
            return exists.Count == 0 ? StatusApplied.NotFound : StatusApplied.NoChange;
        }

        if (failureCode is { } code)
        {
            // Meta aceptó una plantilla de campaña (el destinatario quedó Sent) y avisa después que no la entregó (131042, 131050…). El
            // destinatario pasa a Failed con ese código en la misma transacción: así el detalle lo cuenta, muestra el motivo y un reintento
            // lo toma (salvo el opt-out, CampaignFailureCodes.IsRetriableByHand).
            var text = code.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var reason = CampaignFailureReasons.For(text);
            var messageIds = updated.Select(row => row.Id).ToArray();
            await dbContext.Database.ExecuteSqlAsync(
                $"""
                UPDATE messaging.campaign_recipients SET status = 'Failed', failure_code = {text}, failure_reason = {reason}
                 WHERE message_id = ANY({messageIds}) AND status = 'Sent'
                """, cancellationToken);
        }

        foreach (var row in updated)
        {
            // §7.5: sin subir version ni updated_at; un acuse no puede convertir un resolve en 412.
            await dbContext.Database.ExecuteSqlAsync(
                $"UPDATE messaging.conversations SET last_message_status = {newStatus} WHERE id = {row.ConversationId} AND last_message_id = {row.Id}",
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new StatusApplied(StatusOutcome.Applied, [.. updated.Select(row => row.ConversationId).Distinct()]);
    }

    /// <summary>Spec 2026-10-11 decisión 10: Meta aceptó una plantilla de marketing y la reporta después con este código, porque la persona
    /// apagó las promociones de la organización.</summary>
    public const int MarketingOptOutCode = Modules.Messaging.Domain.MetaErrorCodes.MarketingOptOut;

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

    /// <summary>
    /// Spec 2026-10-14 §6.2, <c>messaging_seen</c>: la persona leyó hasta <paramref name="mid"/>. Instagram no manda <c>delivered</c>
    /// ni un <c>read</c> por mensaje, así que el visto cubre todos nuestros mensajes de esa conversación hasta ese instante, en una
    /// sentencia monótona (sólo Sent o Delivered pasan a Read). La foto de la lista se corrige sin subir <c>version</c>, como los
    /// estados de WhatsApp. <see cref="StatusOutcome.NotFound"/> si el <c>mid</c> no es nuestro (la app del teléfono).
    /// </summary>
    public static async Task<StatusApplied> ApplySeenAsync(
        MessagingDbContext dbContext, Guid connectionId, string userId, string mid, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var updated = await dbContext.Database.SqlQuery<AppliedRow>(
            $"""
            UPDATE messaging.messages AS m
               SET status = 3
              FROM messaging.conversations AS c, messaging.messages AS seen
             WHERE c.id = m.conversation_id AND c.connection_id = {connectionId} AND c.user_id = {userId}
               AND seen.connection_id = {connectionId} AND seen.wamid = {mid} AND seen.direction = 2
               AND m.direction = 2 AND m.status IN (1, 2) AND m.occurred_at <= seen.occurred_at
            RETURNING m.id AS "Id", m.conversation_id AS "ConversationId"
            """).ToListAsync(cancellationToken);

        if (updated.Count == 0)
        {
            var exists = await dbContext.Database.SqlQuery<int>(
                $"""SELECT 1 AS "Value" FROM messaging.messages WHERE connection_id = {connectionId} AND wamid = {mid} AND direction = 2""")
                .ToListAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new StatusApplied(exists.Count == 0 ? StatusOutcome.NotFound : StatusOutcome.NoChange, []);
        }

        var conversationIds = updated.Select(row => row.ConversationId).Distinct().ToArray();
        var messageIds = updated.Select(row => row.Id).ToArray();
        await dbContext.Database.ExecuteSqlAsync(
            $"UPDATE messaging.conversations SET last_message_status = 3 WHERE id = ANY({conversationIds}) AND last_message_id = ANY({messageIds})",
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new StatusApplied(StatusOutcome.Applied, conversationIds);
    }

    /// <summary>Spec 2026-10-14 D-I6, <c>is_deleted</c>: la persona borró su mensaje. El texto se conserva (ya se leyó) y la burbuja lo
    /// tacha por <c>details.deleted</c>. Idempotente. Devuelve la conversación del mensaje, para avisar que cambió (tiempo real), o
    /// <c>null</c> si el mensaje no está (llegó antes que él, o no era de esa persona).</summary>
    public static async Task<Guid?> MarkDeletedAsync(MessagingDbContext dbContext, Guid connectionId, string userId, string mid, CancellationToken cancellationToken)
    {
        const string empty = "{}";
        const string deletedFlag = """{"deleted": true}""";
        var rows = await dbContext.Database.SqlQuery<Guid>(
            $"""
            UPDATE messaging.messages AS m
               SET details = COALESCE(m.details, {empty}::jsonb) || {deletedFlag}::jsonb
              FROM messaging.conversations AS c
             WHERE c.id = m.conversation_id AND c.connection_id = {connectionId} AND c.user_id = {userId}
               AND m.connection_id = {connectionId} AND m.wamid = {mid} AND m.direction = 1
            RETURNING m.conversation_id AS "Value"
            """).ToListAsync(cancellationToken);
        return rows.Count == 1 ? rows[0] : null;
    }

    private sealed record AppliedRow(Guid Id, Guid ConversationId);
}
