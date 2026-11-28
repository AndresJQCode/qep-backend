using Microsoft.EntityFrameworkCore;

namespace Modules.Purchases.Infrastructure.Persistence;

// Mayorista fase 3: guarda de idempotencia y reclamo por consumidor, copia de PipelineInboxMessage (ningún módulo referencia la
// infraestructura de otro): una fila con ProcessedAt en null es un mensaje reclamado y sin terminar; ClaimedUntil es hasta
// cuándo nadie lo vuelve a tomar (su espera antes del reintento) y Attempts cuenta los reclamos.
internal sealed class PurchasesInboxMessage
{
    public string Consumer { get; init; } = string.Empty;

    public Guid MessageId { get; init; }

    public DateTimeOffset? ProcessedAt { get; set; }

    public DateTimeOffset? ClaimedUntil { get; set; }

    public int Attempts { get; set; }
}

// Proyección del outbox de plataforma: la lee el consumidor mayorista y la escribe el publicador de la orden recibida.
internal sealed class PurchasesOutboxRecord
{
    public Guid Id { get; init; }

    public string EventName { get; init; } = string.Empty;

    public string PayloadJson { get; init; } = "{}";

    public DateTimeOffset OccurredAt { get; init; }

    // Sólo los escribe PurchaseOrderEventPublisher (purchases.order.received.v1). EF no deja mapear dos entidades a la misma
    // tabla sin relación, así que la proyección es una sola, de lectura para el consumidor y de escritura para el publicador.
    public string CorrelationId { get; init; } = string.Empty;

    public DateTimeOffset? ProcessedAt { get; init; }

    public int Attempts { get; init; }

    public string? LastError { get; init; }
}

/// <summary>
/// Copia de <c>PipelineInboxClaims</c>: una sola sentencia, fuera de toda transacción explícita, así que se commitea sola y el
/// intento queda contado aunque la unidad de trabajo del mensaje falle. La PK (consumer, message_id) garantiza un solo ganador;
/// el reclamo número n dura <c>leases[n - 1]</c>, y desde el último en adelante, el último.
/// </summary>
internal static class PurchasesInboxClaims
{
    /// <returns>Los intentos contando éste, o <c>null</c> si el mensaje no se pudo reclamar.</returns>
    public static async Task<int?> TryClaimAsync(
        PurchasesDbContext dbContext, string consumer, Guid messageId, DateTimeOffset now, IReadOnlyList<TimeSpan> leases, CancellationToken cancellationToken)
    {
        var leaseArray = leases.ToArray();
        var attempts = await dbContext.Database
            .SqlQuery<int>($"""
                INSERT INTO purchases.inbox_messages (consumer, message_id, claimed_until, attempts)
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
