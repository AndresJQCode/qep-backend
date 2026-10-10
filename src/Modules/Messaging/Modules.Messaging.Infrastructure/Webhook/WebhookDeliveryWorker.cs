using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Messaging.Infrastructure.Options;
using Modules.Messaging.Infrastructure.Persistence;

namespace Modules.Messaging.Infrastructure.Webhook;

/// <summary>
/// Spec 2026-10-09 §8.2: toma un lote de pendientes, reclama cada entrega con una sentencia que se
/// commitea sola (como <c>IdentityInboxClaims</c>, P9) y la procesa en su propio scope. Varios pods
/// pueden correrlo a la vez: el reclamo da un solo ganador. Un fallo deja el reclamo vivo y vuelve al
/// vencer el lease; a los <see cref="MaxAttempts"/> se marca procesada con <c>last_error</c>.
/// </summary>
internal sealed partial class WebhookDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<MessagingWorkerOptions> options,
    ILogger<WebhookDeliveryWorker> logger) : BackgroundService
{
    internal const int BatchSize = 50;

    /// <summary>§8.2: N = 8 intentos (≈ 1 h) para un status cuyo wamid todavía no está.</summary>
    internal const int MaxAttempts = 8;

    internal const int LastErrorMaxLength = 512;

    /// <summary>P15: la espera después del intento n es <c>Leases[n - 1]</c>; desde el último, el último.</summary>
    /// <remarks>
    /// Ojo: el lease es también el tiempo que tiene una pasada para terminar. Una entrega muy grande (hasta
    /// 1000 cambios, §3) puede tardar más de los 10 s del primero; entonces otra réplica la reclama mientras
    /// la primera sigue, y <c>attempts</c> sube sin que haya fallado nada (la idempotencia evita duplicados).
    /// Por eso <c>attempts</c> de la entrega no prueba por sí solo que un status esperó 8 veces: la regla de
    /// rendirse de la Task 13b tiene que mirar las filas de status pendientes, no sólo <c>attempts &gt;= 8</c>.
    /// </remarks>
    internal static readonly IReadOnlyList<TimeSpan> Leases =
    [
        TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(20),
    ];

    // Un solo arreglo para el parámetro del reclamo (CA1861: no armarlo en cada llamada).
    private static readonly TimeSpan[] LeaseArray = [.. Leases];

    public static TimeSpan LeaseFor(int attempt) => Leases[Math.Min(attempt, Leases.Count) - 1];

    [LoggerMessage(Level = LogLevel.Error, Message = "Webhook delivery tick failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook delivery {DeliveryId} failed on attempt {Attempt}; it is retried when its lease expires.")]
    private static partial void LogAttemptFailed(ILogger logger, Exception exception, long deliveryId, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook delivery {DeliveryId} gave up after {Attempt} attempts: {Reason}.")]
    private static partial void LogGaveUp(ILogger logger, long deliveryId, int attempt, string reason);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Value.DeliveryPollSeconds)));
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
        List<long> pending;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
            var now = scope.ServiceProvider.GetRequiredService<IClock>().UtcNow;
            pending = await dbContext.Deliveries.AsNoTracking()
                .Where(delivery => delivery.ProcessedAt == null && (delivery.ClaimedUntil == null || delivery.ClaimedUntil < now))
                .OrderBy(delivery => delivery.Id)
                .Select(delivery => delivery.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
        }

        foreach (var id in pending)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var dbContext = services.GetRequiredService<MessagingDbContext>();
            var clock = services.GetRequiredService<IClock>();
            var claimed = await TryClaimAsync(dbContext, id, clock.UtcNow, cancellationToken);
            if (claimed is null)
            {
                continue; // Otra réplica lo tiene, o ya terminó desde que se armó el lote.
            }

            var (attempts, payload) = claimed.Value;
            try
            {
                var outcome = await services.GetRequiredService<WebhookDeliveryProcessor>().ProcessAsync(id, payload, attempts, cancellationToken);
                if (outcome == DeliveryOutcome.Processed)
                {
                    await MarkProcessedAsync(dbContext, id, clock.UtcNow, null, cancellationToken);
                }
                else if (attempts >= MaxAttempts)
                {
                    LogGaveUp(logger, id, attempts, "status-before-wamid");
                    await MarkProcessedAsync(dbContext, id, clock.UtcNow, $"status-before-wamid: gave up after {MaxAttempts} attempts", cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // El reclamo queda vivo: vuelve al vencer el lease.
                LogAttemptFailed(logger, exception, id, attempts);
                if (attempts >= MaxAttempts)
                {
                    LogGaveUp(logger, id, attempts, exception.GetType().Name);
                    await MarkProcessedAsync(dbContext, id, clock.UtcNow, Truncate(exception.GetType().Name + ": " + exception.Message, LastErrorMaxLength), cancellationToken);
                }
            }
        }
    }

    /// <summary>P9: la regla de IdentityInboxClaims sobre webhook_deliveries, con leases por intento.</summary>
    private static async Task<(int Attempts, string Payload)?> TryClaimAsync(MessagingDbContext dbContext, long id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var leases = LeaseArray;
        var rows = await dbContext.Database.SqlQuery<ClaimRow>(
            $"""
            UPDATE messaging.webhook_deliveries
               SET claimed_until = {now} + ({leases})[LEAST(attempts + 1, cardinality({leases}))],
                   attempts = attempts + 1
             WHERE id = {id} AND processed_at IS NULL AND (claimed_until IS NULL OR claimed_until < {now})
            RETURNING attempts AS "Attempts", payload::text AS "Payload"
            """).ToListAsync(cancellationToken);
        return rows.Count == 1 ? (rows[0].Attempts, rows[0].Payload) : null;
    }

    private static Task<int> MarkProcessedAsync(MessagingDbContext dbContext, long id, DateTimeOffset now, string? lastError, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlAsync(
            $"UPDATE messaging.webhook_deliveries SET processed_at = {now}, last_error = {lastError} WHERE id = {id}", cancellationToken);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private sealed record ClaimRow(int Attempts, string Payload);
}
