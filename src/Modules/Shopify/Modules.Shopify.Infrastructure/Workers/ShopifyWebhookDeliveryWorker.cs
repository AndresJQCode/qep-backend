using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Shopify.Application;

namespace Modules.Shopify.Infrastructure.Workers;

/// <summary>
/// Spec 2026-10-15 §5.2, copia de <c>WebhookDeliveryWorker</c> de Messaging (H15): toma lotes de <see cref="BatchSize"/>, reclama
/// cada entrega con un <c>UPDATE</c> que se commitea solo (un solo ganador entre réplicas) y la procesa en su propio scope. Lo que
/// el procesador devuelve se cierra en el acto, con su <c>ignored_reason</c> o su <c>last_error</c>: así termina un payload que
/// no se puede leer, en el primer intento (Review Focus 2). Lo que lanza deja el reclamo vivo y vuelve al vencer el lease, que
/// crece por intento; a los <see cref="MaxAttempts"/> se cierra con <c>last_error</c>.
/// </summary>
internal sealed partial class ShopifyWebhookDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<ShopifyWebhookOptions> options,
    ILogger<ShopifyWebhookDeliveryWorker> logger) : BackgroundService
{
    internal const int BatchSize = 50;

    internal const int MaxAttempts = 8;

    /// <summary>Tope de lotes llenos seguidos en una pasada (1000 entregas), como Messaging: el resto espera al tick siguiente.</summary>
    internal const int MaxFullBatchesPerTick = 20;

    /// <summary>La espera después del intento n es <c>Leases[n - 1]</c>; desde el último, el último (≈ 54 min en total).</summary>
    internal static readonly IReadOnlyList<TimeSpan> Leases =
    [
        TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(20),
    ];

    public static TimeSpan LeaseFor(int attempt) => Leases[Math.Min(attempt, Leases.Count) - 1];

    [LoggerMessage(Level = LogLevel.Error, Message = "Shopify webhook delivery tick failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shopify webhook delivery {DeliveryId} failed on attempt {Attempt}; it is retried when its lease expires.")]
    private static partial void LogAttemptFailed(ILogger logger, Exception exception, long deliveryId, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shopify webhook delivery {DeliveryId} gave up after {Attempt} attempts: {Reason}.")]
    private static partial void LogGaveUp(ILogger logger, long deliveryId, int attempt, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shopify webhook delivery {DeliveryId} closed without retry: {Reason}.")]
    private static partial void LogClosedWithError(ILogger logger, long deliveryId, string reason);

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

    /// <summary>Una pasada (internal para las pruebas): mientras el lote vuelve lleno, sigue en el acto.</summary>
    internal async Task DrainAsync(CancellationToken cancellationToken)
    {
        for (var batch = 0; batch < MaxFullBatchesPerTick; batch++)
        {
            if (await DrainBatchAsync(cancellationToken) < BatchSize)
            {
                return;
            }
        }
    }

    private async Task<int> DrainBatchAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<long> pending;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var now = scope.ServiceProvider.GetRequiredService<IClock>().UtcNow;
            pending = await scope.ServiceProvider.GetRequiredService<IShopifyWebhookDeliveryQueue>()
                .ListPendingAsync(now, BatchSize, cancellationToken);
        }

        foreach (var id in pending)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var queue = services.GetRequiredService<IShopifyWebhookDeliveryQueue>();
            var clock = services.GetRequiredService<IClock>();
            var claim = await queue.TryClaimAsync(id, clock.UtcNow, cancellationToken);
            if (claim is null)
            {
                continue; // Otra réplica la tiene, o ya terminó desde que se armó el lote.
            }

            try
            {
                var result = await services.GetRequiredService<ShopifyWebhookDeliveryProcessor>().ProcessAsync(claim.Delivery, cancellationToken);
                if (result.LastError is { } closedWith)
                {
                    LogClosedWithError(logger, id, closedWith);
                }

                await queue.MarkProcessedAsync(id, clock.UtcNow, result.IgnoredReason, result.LastError, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // El reclamo queda vivo: vuelve al vencer el lease.
                LogAttemptFailed(logger, exception, id, claim.Attempts);
                if (claim.Attempts >= MaxAttempts)
                {
                    LogGaveUp(logger, id, claim.Attempts, exception.GetType().Name);
                    await queue.MarkProcessedAsync(
                        id, clock.UtcNow, null, Truncate($"{exception.GetType().Name}: {exception.Message}"), cancellationToken);
                }
            }
        }

        return pending.Count;
    }

    private static string Truncate(string value) =>
        value.Length <= ShopifyWebhookDeliveryProcessor.LastErrorMaxLength ? value : value[..ShopifyWebhookDeliveryProcessor.LastErrorMaxLength];
}
