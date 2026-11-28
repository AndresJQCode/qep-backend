using Microsoft.EntityFrameworkCore;

namespace Modules.Wms.Infrastructure.Persistence;

// Inventario básico: guarda de idempotencia y reclamo por consumidor, copia de PurchasesInboxMessage (ningún módulo referencia la
// infraestructura de otro): una fila con ProcessedAt en null es un mensaje reclamado y sin terminar; ClaimedUntil es hasta cuándo
// nadie lo vuelve a tomar (su espera antes del reintento) y Attempts cuenta los reclamos.
internal sealed class WmsInboxMessage
{
    public string Consumer { get; init; } = string.Empty;

    public Guid MessageId { get; init; }

    public DateTimeOffset? ProcessedAt { get; set; }

    public DateTimeOffset? ClaimedUntil { get; set; }

    public int Attempts { get; set; }
}

/// <summary>
/// Copia de <c>PurchasesInboxClaims</c> con el reclamo separado de la espera de reintento: una sola sentencia, fuera de toda
/// transacción explícita, así que se commitea sola y el intento queda contado aunque la unidad de trabajo del mensaje falle. La PK
/// (consumer, message_id) garantiza un solo ganador. El reclamo dura <c>lease</c> (lo que tarda en confirmar un mensaje, incluida una
/// bodega por aprovisionar); la espera antes de reintentar un fallo la fija <see cref="ScheduleRetryAsync"/>.
/// </summary>
internal static class WmsInboxClaims
{
    /// <returns>Los intentos contando éste, o <c>null</c> si el mensaje no se pudo reclamar.</returns>
    public static async Task<int?> TryClaimAsync(
        WmsDbContext dbContext, string consumer, Guid messageId, DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken)
    {
        var attempts = await dbContext.Database
            .SqlQuery<int>($"""
                INSERT INTO wms.inbox_messages (consumer, message_id, claimed_until, attempts)
                VALUES ({consumer}, {messageId}, {now} + {lease}, 1)
                ON CONFLICT (consumer, message_id) DO UPDATE
                    SET claimed_until = {now} + {lease},
                        attempts = inbox_messages.attempts + 1
                    WHERE inbox_messages.processed_at IS NULL
                      AND inbox_messages.claimed_until < {now}
                RETURNING attempts AS "Value"
                """)
            // Sin FirstOrDefaultAsync: componer sobre el SQL lo envolvería en un SELECT, y Postgres lo rechaza para un INSERT ... RETURNING.
            .ToListAsync(cancellationToken);

        return attempts.Count == 1 ? attempts[0] : null;
    }

    /// <summary>Tras un fallo, el mensaje vuelve cuando pase <paramref name="delay"/> (la curva de reintento), no cuando venza el reclamo.</summary>
    public static Task<int> ScheduleRetryAsync(
        WmsDbContext dbContext, string consumer, Guid messageId, DateTimeOffset now, TimeSpan delay, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlAsync(
            $"""
            UPDATE wms.inbox_messages SET claimed_until = {now} + {delay}
            WHERE consumer = {consumer} AND message_id = {messageId} AND processed_at IS NULL
            """,
            cancellationToken);

    /// <summary>Marca terminado el mensaje <b>sólo si nadie lo terminó antes</b>, dentro de la transacción de quien llama. Devuelve
    /// <c>false</c> si otra réplica ya lo confirmó: quien llama debe deshacer su transacción entera (un reclamo vencido a mitad de
    /// camino no puede contar dos veces).</summary>
    public static async Task<bool> TryMarkProcessedAsync(
        WmsDbContext dbContext, string consumer, Guid messageId, DateTimeOffset now, CancellationToken cancellationToken) =>
        await dbContext.Database.ExecuteSqlAsync(
            $"""
            UPDATE wms.inbox_messages SET processed_at = {now}
            WHERE consumer = {consumer} AND message_id = {messageId} AND processed_at IS NULL
            """,
            cancellationToken) == 1;
}
