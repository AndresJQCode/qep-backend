using Microsoft.EntityFrameworkCore;

namespace Modules.Notifications.Infrastructure.Persistence;

/// <summary>
/// El reclamo de un mensaje del outbox por un consumidor (spec 2026-09-13, A1). Es una sola
/// sentencia, fuera de toda transacción explícita: así se commitea sola, y el lease ya se ve desde
/// otros procesos antes de que este mande el correo. Mismo criterio que
/// ExportJobQueue.ClaimNextAsync.
///
/// La PK (consumer, message_id) garantiza un solo ganador. El INSERT gana si no hay fila. El
/// ON CONFLICT sólo actualiza —y sólo entonces devuelve algo— si la fila existente no está procesada
/// y su lease venció. En cualquier otro caso no devuelve nada: otra réplica lo tiene, o ya se terminó.
/// </summary>
internal static class InboxClaims
{
    /// <returns>Los intentos contando éste, o <c>null</c> si el mensaje no se pudo reclamar.</returns>
    public static async Task<int?> TryClaimAsync(
        NotificationsDbContext dbContext,
        string consumer,
        Guid messageId,
        DateTimeOffset now,
        TimeSpan lease,
        CancellationToken cancellationToken)
    {
        var leaseUntil = now.Add(lease);
        var attempts = await dbContext.Database
            .SqlQuery<int>($"""
                INSERT INTO notifications.inbox_messages (consumer, message_id, claimed_until, attempts)
                VALUES ({consumer}, {messageId}, {leaseUntil}, 1)
                ON CONFLICT (consumer, message_id) DO UPDATE
                    SET claimed_until = {leaseUntil},
                        attempts = inbox_messages.attempts + 1
                    WHERE inbox_messages.processed_at IS NULL
                      AND inbox_messages.claimed_until < {now}
                RETURNING attempts AS "Value"
                """)
            // Sin FirstOrDefaultAsync: componer sobre el SQL lo envolvería en un SELECT, y Postgres lo
            // rechaza para un INSERT ... RETURNING. Mismo criterio que CucGenerator.NextBatchAsync.
            .ToListAsync(cancellationToken);

        return attempts.Count == 1 ? attempts[0] : null;
    }
}
