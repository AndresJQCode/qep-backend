using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Messaging.Infrastructure.Options;
using Modules.Messaging.Infrastructure.Persistence;

namespace Modules.Messaging.Infrastructure.Media;

/// <summary>§8.6: reclama pendientes con FOR UPDATE SKIP LOCKED y next_attempt_at = now + lease[n] (P15:
/// 1 min, 5 min, 30 min, 2 h, 6 h, 6 h…), y copia cada uno en su scope. Varias réplicas pueden correrlo a
/// la vez: el reclamo da un solo ganador por fila.</summary>
internal sealed partial class MediaCopyWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<MessagingWorkerOptions> options,
    ILogger<MediaCopyWorker> logger) : BackgroundService
{
    internal const int BatchSize = 20;

    /// <summary>P15: la espera después del intento n es <c>Leases[n - 1]</c>; desde el último, el último.
    /// El lease es también el tiempo que tiene una copia para terminar antes de que otra réplica la retome.</summary>
    internal static readonly IReadOnlyList<TimeSpan> Leases =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), TimeSpan.FromHours(6)];

    // Un solo arreglo para el parámetro del reclamo (CA1861: no armarlo en cada llamada).
    private static readonly TimeSpan[] LeaseArray = [.. Leases];

    [LoggerMessage(Level = LogLevel.Error, Message = "Media copy tick failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Media copy of message {MessageId} threw; it is retried when its lease expires.")]
    private static partial void LogCopyFailed(ILogger logger, Exception exception, Guid messageId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Value.MediaPollSeconds)));
        do
        {
            try
            {
                await DrainAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogTickFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Una pasada: internal para las pruebas (P10).</summary>
    internal async Task DrainAsync(CancellationToken cancellationToken)
    {
        List<PendingMedia> claimed;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
            var now = scope.ServiceProvider.GetRequiredService<IClock>().UtcNow;
            var leases = LeaseArray;
            // Reclamo y lectura en una sentencia que se commitea sola: otra réplica no toma la misma fila.
            claimed = await dbContext.Database.SqlQuery<PendingMedia>(
                $"""
                UPDATE messaging.message_media AS media
                   SET next_attempt_at = {now} + ({leases})[LEAST(media.attempts + 1, cardinality({leases}))],
                       attempts = media.attempts + 1
                  FROM (SELECT message_id FROM messaging.message_media
                         WHERE stored_at IS NULL AND next_attempt_at <= {now}
                         ORDER BY next_attempt_at LIMIT {BatchSize} FOR UPDATE SKIP LOCKED) AS pending
                  JOIN messaging.messages AS message ON message.id = pending.message_id
                 WHERE media.message_id = pending.message_id
                RETURNING media.message_id AS "MessageId", message.tenant_id AS "TenantId", message.connection_id AS "ConnectionId",
                          media.meta_media_id AS "MetaMediaId", message.occurred_at AS "OccurredAt", media.attempts AS "Attempts"
                """).ToListAsync(cancellationToken);
        }

        foreach (var pending in claimed)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            try
            {
                await scope.ServiceProvider.GetRequiredService<MediaCopyProcessor>().CopyOneAsync(pending, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogCopyFailed(logger, exception, pending.MessageId);
            }
        }
    }
}
