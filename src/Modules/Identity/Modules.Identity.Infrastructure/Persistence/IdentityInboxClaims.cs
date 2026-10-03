using Microsoft.EntityFrameworkCore;

namespace Modules.Identity.Infrastructure.Persistence;

/// <summary>
/// El reclamo de un mensaje del outbox por un consumidor de Identity. Es el mismo mecanismo que
/// <c>InboxClaims</c> de Notifications (spec 2026-09-13, A1), copiado y no compartido porque
/// ningún módulo referencia la infraestructura de otro: una sola sentencia, fuera de toda
/// transacción explícita, así que se commitea sola. Por eso el intento queda contado aunque
/// después la unidad de trabajo del mensaje falle y se descarte entera.
///
/// La PK (consumer, message_id) garantiza un solo ganador. El INSERT gana si no hay fila. El
/// ON CONFLICT sólo actualiza —y sólo entonces devuelve algo— si la fila existente no está
/// procesada y su lease venció. En cualquier otro caso no devuelve nada: otra réplica lo tiene,
/// todavía no le toca el reintento, o ya se terminó.
///
/// La diferencia con Notifications es el lease: allá es uno fijo; acá crece con cada intento,
/// porque además de proteger al que procesa es la espera antes del reintento. El reclamo número
/// n dura <c>leases[n - 1]</c>, y desde el último en adelante, el último.
/// </summary>
internal static class IdentityInboxClaims
{
    /// <returns>Los intentos contando éste, o <c>null</c> si el mensaje no se pudo reclamar.</returns>
    public static async Task<int?> TryClaimAsync(
        IdentityDbContext dbContext,
        string consumer,
        Guid messageId,
        DateTimeOffset now,
        IReadOnlyList<TimeSpan> leases,
        CancellationToken cancellationToken)
    {
        // Un arreglo interval[] de Postgres, que indexa desde 1: el reclamo n usa leases[n].
        var leaseArray = leases.ToArray();
        var attempts = await dbContext.Database
            .SqlQuery<int>($"""
                INSERT INTO identity.inbox_messages (consumer, message_id, claimed_until, attempts)
                VALUES ({consumer}, {messageId}, {now} + ({leaseArray})[1], 1)
                ON CONFLICT (consumer, message_id) DO UPDATE
                    SET claimed_until = {now} + ({leaseArray})[
                            LEAST(inbox_messages.attempts + 1, cardinality({leaseArray}))],
                        attempts = inbox_messages.attempts + 1
                    WHERE inbox_messages.processed_at IS NULL
                      AND inbox_messages.claimed_until < {now}
                RETURNING attempts AS "Value"
                """)
            // Sin FirstOrDefaultAsync: componer sobre el SQL lo envolvería en un SELECT, y Postgres
            // lo rechaza para un INSERT ... RETURNING. Mismo criterio que InboxClaims.
            .ToListAsync(cancellationToken);

        return attempts.Count == 1 ? attempts[0] : null;
    }
}
