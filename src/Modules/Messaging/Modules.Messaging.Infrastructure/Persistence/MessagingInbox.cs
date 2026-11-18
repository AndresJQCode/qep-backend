using Microsoft.EntityFrameworkCore;

namespace Modules.Messaging.Infrastructure.Persistence;

// Guarda de idempotencia y reclamo por consumidor, copia de PipelineInboxMessage (spec 2026-10-29: Messaging consume
// integrations.connection.whatsapp_coexistence_connected.v1). Una fila con ProcessedAt en null es un mensaje reclamado y sin
// terminar; ClaimedUntil es hasta cuándo nadie lo vuelve a tomar (su espera antes del reintento) y Attempts cuenta los reclamos.
internal sealed class MessagingInboxMessage
{
    public string Consumer { get; init; } = string.Empty;

    public Guid MessageId { get; init; }

    public DateTimeOffset? ProcessedAt { get; set; }

    public DateTimeOffset? ClaimedUntil { get; set; }

    public int Attempts { get; set; }
}

// Proyección del outbox de plataforma (Tenancy es dueño de la tabla, ExcludeFromMigrations). Los consumidores del módulo la leen y, desde el
// plan 2026-11-17 (Task 8), TemplateHeaderImageReleasePublisher también escribe en ella, en la transacción de la plantilla: por eso lleva
// correlation_id y attempts, que la tabla tiene NOT NULL (InitialPlatform), además de las columnas de proceso, como CatalogOutboxMessage.
internal sealed class MessagingOutboxRecord
{
    public Guid Id { get; init; }

    public string EventName { get; init; } = string.Empty;

    public string PayloadJson { get; init; } = "{}";

    public string CorrelationId { get; init; } = string.Empty;

    public DateTimeOffset OccurredAt { get; init; }

    public DateTimeOffset? ProcessedAt { get; init; }

    public int Attempts { get; init; }

    public string? LastError { get; init; }
}

/// <summary>
/// Copia de <c>PipelineInboxClaims</c> (ningún módulo referencia la infraestructura de otro): una sola sentencia, fuera de toda
/// transacción explícita, así que se commitea sola y el intento queda contado aunque la unidad de trabajo del mensaje falle. La PK
/// (consumer, message_id) garantiza un solo ganador; el reclamo número n dura <c>leases[n - 1]</c>, y desde el último en adelante,
/// el último.
/// </summary>
internal static class MessagingInboxClaims
{
    /// <returns>Los intentos contando éste, o <c>null</c> si el mensaje no se pudo reclamar.</returns>
    public static async Task<int?> TryClaimAsync(
        MessagingDbContext dbContext, string consumer, Guid messageId, DateTimeOffset now, IReadOnlyList<TimeSpan> leases, CancellationToken cancellationToken)
    {
        var leaseArray = leases.ToArray();
        var attempts = await dbContext.Database
            .SqlQuery<int>($"""
                INSERT INTO messaging.inbox_messages (consumer, message_id, claimed_until, attempts)
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
