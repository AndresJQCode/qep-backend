using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Wms.Infrastructure.Persistence;
using Npgsql;

namespace Modules.Wms.Infrastructure.Messaging;

/// <summary>
/// Copia de <c>ClaimedOutboxConsumer</c> de Purchases (ningún módulo referencia la infraestructura de otro), con el orden que pide el
/// inventario básico (spec §5.2): armar el lote, reclamar cada mensaje, procesarlo en su propio scope y no dejar que una falla corte el
/// lote.
/// </summary>
/// <remarks>
/// <para><b>Lote.</b> Los mensajes de sus eventos posteriores a la marca de arranque, sin fila terminada en el inbox ni reclamo
/// vigente, ordenados por (<c>OccurredAt</c>, <c>version</c> del payload, <c>Id</c>). Sólo entra un mensaje si no hay otro <b>más
/// viejo del mismo documento</b> (misma <c>correlation_id</c>) sin procesar: un reintento o un lease vencido no puede invertir
/// <c>prepared</c> y <c>edited</c>. Documentos distintos avanzan en el mismo lote.</para>
/// <para><b>Reclamo.</b> <see cref="WmsInboxClaims"/> antes de procesar: una sentencia que se commitea sola y suma el intento. Nunca se
/// abandona un mensaje.</para>
/// <para><b>Un scope por mensaje.</b> Con su propio <see cref="WmsDbContext"/>: lo que dejó rastreado un mensaje que falla muere con
/// su scope.</para>
/// </remarks>
internal sealed class ClaimedOutboxConsumer(
    IServiceScopeFactory scopeFactory,
    string consumer,
    IReadOnlyList<string> eventNames,
    IReadOnlyList<TimeSpan> leases)
{
    public const int BatchSize = 20;

    /// <summary>Desde qué intento una falla se loguea en Error y el documento queda <c>Parked</c>: el primero que deja la espera de
    /// 5 minutos por delante (la curva es la de Purchases: 5 s, 30 s, 5 minutos).</summary>
    public const int ParkAfterAttempts = 3;

    // La versión del payload como número; un payload sin ella (o con otra cosa) cuenta como 0 y no tumba la consulta del lote.
    private static string VersionOf(string alias) =>
        $"CASE WHEN {alias}.payload->>'version' ~ '^[0-9]+$' AND length({alias}.payload->>'version') <= 18 "
        + $"THEN ({alias}.payload->>'version')::bigint ELSE 0 END";

    public IReadOnlyList<TimeSpan> Leases => leases;

    public async Task<bool> ProcessBatchAsync(
        DateTimeOffset startAt,
        Func<ClaimedMessage, CancellationToken, Task> process,
        Func<Exception, OutboxRow, int?, CancellationToken, Task> onFailure,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(onFailure);
        List<OutboxRow> pending;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<WmsDbContext>();
            var now = scope.ServiceProvider.GetRequiredService<IClock>().UtcNow;
            pending = await ReadBatchAsync(dbContext, startAt, now, cancellationToken);
        }

        var processed = 0;
        foreach (var record in pending)
        {
            await using var recordScope = scopeFactory.CreateAsyncScope();
            var services = recordScope.ServiceProvider;
            int? attempts = null;
            try
            {
                var dbContext = services.GetRequiredService<WmsDbContext>();
                var now = services.GetRequiredService<IClock>().UtcNow;
                attempts = await WmsInboxClaims.TryClaimAsync(dbContext, consumer, record.Id, now, leases, cancellationToken);
                if (attempts is null)
                {
                    // Otra réplica lo tiene, o ya se terminó desde que se armó el lote.
                    continue;
                }

                await process(new ClaimedMessage(services, dbContext, record, now, attempts.Value), cancellationToken);
                processed++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                // El intento ya quedó contado en el reclamo: el mensaje vuelve cuando ese reclamo venza, siempre.
                await onFailure(exception, record, attempts, cancellationToken);
            }
        }

        // Hay más si algo avanzó: el siguiente mensaje de un documento sólo entra al lote cuando el anterior se procesó. Un lote donde
        // todo falló no repite de inmediato: el reintento espera su reclamo.
        return processed > 0;
    }

    /// <summary>Marca como terminada la fila que dejó el reclamo, sin guardar: quien llama la guarda en el mismo SaveChanges que su
    /// efecto, así que un mensaje procesado nunca queda sin marcar.</summary>
    public async Task MarkProcessedAsync(ClaimedMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var entry = await message.DbContext.Inbox.SingleAsync(
            candidate => candidate.Consumer == consumer && candidate.MessageId == message.Record.Id, cancellationToken);
        entry.ProcessedAt = message.Now;
    }

    private async Task<List<OutboxRow>> ReadBatchAsync(
        WmsDbContext dbContext, DateTimeOffset startAt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var rowVersion = VersionOf("o");
        var olderVersion = VersionOf("p");
        var sql = $"""
            SELECT o.id AS "Id", o.event_name AS "EventName", o.payload::text AS "PayloadJson",
                   o.correlation_id AS "CorrelationId", o.occurred_at AS "OccurredAt"
            FROM platform.outbox_messages AS o
            WHERE o.event_name = ANY(@names)
              AND o.occurred_at >= @startAt
              AND NOT EXISTS (
                  SELECT 1 FROM wms.inbox_messages AS i
                  WHERE i.consumer = @consumer AND i.message_id = o.id
                    AND (i.processed_at IS NOT NULL OR i.claimed_until >= @now))
              AND NOT EXISTS (
                  SELECT 1 FROM platform.outbox_messages AS p
                  WHERE p.correlation_id = o.correlation_id
                    AND p.event_name = ANY(@names)
                    AND p.occurred_at >= @startAt
                    AND (p.occurred_at, {olderVersion}, p.id) < (o.occurred_at, {rowVersion}, o.id)
                    AND NOT EXISTS (
                        SELECT 1 FROM wms.inbox_messages AS pi
                        WHERE pi.consumer = @consumer AND pi.message_id = p.id AND pi.processed_at IS NOT NULL))
            ORDER BY o.occurred_at, {rowVersion}, o.id
            LIMIT {BatchSize}
            """;

        return await dbContext.Database
            .SqlQueryRaw<OutboxRow>(
                sql,
                new NpgsqlParameter<string[]>("names", [.. eventNames]),
                new NpgsqlParameter<DateTimeOffset>("startAt", startAt.ToUniversalTime()),
                new NpgsqlParameter<DateTimeOffset>("now", now.ToUniversalTime()),
                new NpgsqlParameter<string>("consumer", consumer))
            .ToListAsync(cancellationToken);
    }
}

/// <summary>Lo que el consumidor lee de un mensaje del outbox.</summary>
internal sealed record OutboxRow(Guid Id, string EventName, string PayloadJson, string CorrelationId, DateTimeOffset OccurredAt);

/// <summary>Un mensaje ya reclamado, con el scope en el que se procesa, el instante del reclamo y los intentos contando éste.</summary>
internal sealed record ClaimedMessage(
    IServiceProvider Services, WmsDbContext DbContext, OutboxRow Record, DateTimeOffset Now, int Attempts);
