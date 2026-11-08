using Microsoft.EntityFrameworkCore;
using Modules.Buyers.Infrastructure.Persistence;

namespace Modules.Buyers.Infrastructure.Signup;

/// <summary>
/// Copia de <c>InboxClaims</c> de Notifications sobre <c>buyers.inbox_messages</c>. Una sola sentencia, fuera de toda transacción
/// explícita: se commitea sola y el lease ya se ve desde otras réplicas. La PK (consumer, message_id) garantiza un solo ganador; el
/// ON CONFLICT sólo actualiza (y sólo entonces devuelve algo) si la fila existente no está procesada y su lease venció.
/// </summary>
internal static class BuyersInboxClaims
{
    /// <returns>Los intentos contando éste, o <c>null</c> si el mensaje no se pudo reclamar.</returns>
    public static async Task<int?> TryClaimAsync(
        BuyersDbContext dbContext,
        string consumer,
        Guid messageId,
        DateTimeOffset now,
        TimeSpan lease,
        CancellationToken cancellationToken)
    {
        var leaseUntil = now.Add(lease);
        var attempts = await dbContext.Database
            .SqlQuery<int>($"""
                INSERT INTO buyers.inbox_messages (consumer, message_id, claimed_until, attempts)
                VALUES ({consumer}, {messageId}, {leaseUntil}, 1)
                ON CONFLICT (consumer, message_id) DO UPDATE
                    SET claimed_until = {leaseUntil},
                        attempts = inbox_messages.attempts + 1
                    WHERE inbox_messages.processed_at IS NULL
                      AND inbox_messages.claimed_until < {now}
                RETURNING attempts AS "Value"
                """)
            // Sin FirstOrDefaultAsync: Postgres rechaza un SELECT que envuelva un INSERT ... RETURNING.
            .ToListAsync(cancellationToken);

        return attempts.Count == 1 ? attempts[0] : null;
    }
}
