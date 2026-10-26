using Microsoft.EntityFrameworkCore;

namespace Modules.Shopify.Infrastructure.Persistence;

// Guarda de idempotencia y reclamo por consumidor, copia de PipelineInboxMessage (ningún módulo referencia la infraestructura de
// otro): una fila con ProcessedAt en null es un mensaje reclamado y sin terminar; ClaimedUntil es hasta cuándo nadie lo vuelve a
// tomar (su espera antes del reintento) y Attempts cuenta los reclamos.
internal sealed class ShopifyInboxMessage
{
    public string Consumer { get; init; } = string.Empty;

    public Guid MessageId { get; init; }

    public DateTimeOffset? ProcessedAt { get; set; }

    public DateTimeOffset? ClaimedUntil { get; set; }

    public int Attempts { get; set; }
}

// Proyección de sólo lectura del outbox de plataforma, consumida independiente por este módulo.
internal sealed class ShopifyOutboxRecord
{
    public Guid Id { get; init; }

    public string EventName { get; init; } = string.Empty;

    public string PayloadJson { get; init; } = "{}";

    public DateTimeOffset OccurredAt { get; init; }
}

/// <summary>
/// Copia de <c>PipelineInboxClaims</c>: una sola sentencia, fuera de toda transacción explícita, así que se commitea sola y el
/// intento queda contado aunque la unidad de trabajo del mensaje falle. La PK (consumer, message_id) garantiza un solo ganador; el
/// reclamo número n dura <c>leases[n - 1]</c>, y desde el último en adelante, el último.
/// </summary>
internal static class ShopifyInboxClaims
{
    /// <returns>Los intentos contando éste, o <c>null</c> si el mensaje no se pudo reclamar.</returns>
    public static async Task<int?> TryClaimAsync(
        ShopifyDbContext dbContext, string consumer, Guid messageId, DateTimeOffset now, IReadOnlyList<TimeSpan> leases, CancellationToken cancellationToken)
    {
        var leaseArray = leases.ToArray();
        var attempts = await dbContext.Database
            .SqlQuery<int>($"""
                INSERT INTO shopify.inbox_messages (consumer, message_id, claimed_until, attempts)
                VALUES ({consumer}, {messageId}, {now} + ({leaseArray})[1], 1)
                ON CONFLICT (consumer, message_id) DO UPDATE
                    SET claimed_until = {now} + ({leaseArray})[
                            LEAST(inbox_messages.attempts + 1, cardinality({leaseArray}))],
                        attempts = inbox_messages.attempts + 1
                    WHERE inbox_messages.processed_at IS NULL
                      AND inbox_messages.claimed_until < {now}
                RETURNING attempts AS "Value"
                """)
            // Sin FirstOrDefaultAsync: componer sobre el SQL lo envolvería en un SELECT, y Postgres lo rechaza para un
            // INSERT ... RETURNING. Mismo criterio que PipelineInboxClaims.
            .ToListAsync(cancellationToken);

        return attempts.Count == 1 ? attempts[0] : null;
    }
}
