using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Modules.Shopify.Application;
using Modules.Shopify.Domain;

namespace Modules.Shopify.Infrastructure.Workers;

/// <summary>
/// Spec 2026-11-14 §5 y plan D14, copia de <see cref="ShopifyCityCorrectionWorker"/>: toma lotes de <see cref="BatchSize"/> envíos
/// pendientes, reclama cada uno con un <c>UPDATE</c> que se commitea solo (un solo ganador entre réplicas) y lo procesa en su propio
/// scope. El reclamo toma el envío por <see cref="InFlightLease"/> mientras está en vuelo. Lo que el procesador cierra (<c>Sent</c>,
/// <c>Failed</c>) o retiene (<c>Held</c>, owner 2026-11-14) lo guarda él; un retenido no vuelve a salir de acá: lo sueltan los
/// endpoints de sincronización. Un <c>Retry</c> o una excepción reprograman el envío con la espera de la escalera
/// (<see cref="Leases"/>, o el <c>Retry-After</c> de Shopify si es mayor); en el intento <see cref="MaxAttempts"/> el envío termina
/// <c>Failed</c> con <c>retries_exhausted</c>.
/// <para>Toda corrección encolada mientras el envío está pendiente sube su versión, también con los mismos grupos
/// (<c>ShopifyOrderPush.Merge</c>): el cierre del procesador choca (<c>concurrency.conflict</c>), el envío sigue <c>Pending</c> y
/// vuelve a salir con lo último del pedido. Si ese choque cae en el último intento, el envío termina <c>retries_exhausted</c> aunque
/// el <c>orderUpdate</c> haya salido (review Minor 6, sin cerrar).</para>
/// </summary>
internal sealed partial class ShopifyOrderPushWorker(
    IServiceScopeFactory scopeFactory, ILogger<ShopifyOrderPushWorker> logger) : BackgroundService
{
    internal const int BatchSize = 50;

    internal const int MaxAttempts = 8;

    /// <summary>Tope de lotes llenos seguidos en una pasada (1000 envíos): el resto espera al tick siguiente.</summary>
    internal const int MaxFullBatchesPerTick = 20;

    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    /// <summary>El lease del reclamo, mientras el envío está en vuelo (review Minor 2): 2 minutos, holgado frente a un intento, que es
    /// el timeout de <c>shopify.admin</c> (10 s) más las lecturas de la conexión, la fila, el pedido y Geography y el guardado. Con el
    /// lease de la escalera (10 s el primero) otra réplica podía reclamar el envío en vuelo y mandar un <c>orderUpdate</c>
    /// duplicado. Si el proceso muere a mitad, el envío vuelve a los 2 minutos.</summary>
    internal static readonly TimeSpan InFlightLease = TimeSpan.FromMinutes(2);

    /// <summary>La espera después del intento n que terminó en <c>Retry</c> o con una excepción es <c>Leases[n - 1]</c> (≈ 8 h hasta
    /// el octavo), o el <c>Retry-After</c> de Shopify si es mayor; la escribe el worker al terminar el intento.</summary>
    internal static readonly IReadOnlyList<TimeSpan> Leases =
    [
        TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(30), TimeSpan.FromHours(1), TimeSpan.FromHours(2), TimeSpan.FromHours(4),
    ];

    [LoggerMessage(Level = LogLevel.Error, Message = "Shopify order push tick failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shopify order push {PushId} failed on attempt {Attempt} of {MaxAttempts}; it is retried later.")]
    private static partial void LogAttemptFailed(ILogger logger, Exception exception, Guid pushId, int attempt, int maxAttempts);

    [LoggerMessage(Level = LogLevel.Error, Message = "Shopify order push {PushId} gave up on its last attempt ({Attempt}): retries_exhausted.")]
    private static partial void LogGaveUp(ILogger logger, Guid pushId, int attempt);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
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
        IReadOnlyList<Guid> pending;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var now = scope.ServiceProvider.GetRequiredService<IClock>().UtcNow;
            pending = await scope.ServiceProvider.GetRequiredService<IShopifyOrderPushQueue>()
                .ListPendingAsync(now, BatchSize, cancellationToken);
        }

        foreach (var id in pending)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var queue = services.GetRequiredService<IShopifyOrderPushQueue>();
            var clock = services.GetRequiredService<IClock>();
            if (await queue.TryClaimAsync(id, clock.UtcNow, cancellationToken) is not { } attempt)
            {
                continue; // Otra réplica lo tiene, o ya terminó desde que se armó el lote.
            }

            ShopifyOrderUpdateResult? result = null;
            try
            {
                var push = await services.GetRequiredService<IShopifyOrderPushRepository>().FindByIdAsync(id, cancellationToken);
                if (push is { Status: ShopifyOrderPushStatus.Pending })
                {
                    result = await services.GetRequiredService<ShopifyOrderPushProcessor>().ProcessAsync(push, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogAttemptFailed(logger, exception, id, attempt, MaxAttempts);
                result = new ShopifyOrderUpdateResult(ShopifyOrderUpdateKind.Retry, null, null);
            }

            if (result is not { Kind: ShopifyOrderUpdateKind.Retry })
            {
                continue;
            }

            if (attempt >= MaxAttempts)
            {
                LogGaveUp(logger, id, attempt);
                await queue.MarkFailedAsync(id, clock.UtcNow, ShopifyPushFailureCodes.RetriesExhausted, cancellationToken);
            }
            else
            {
                var wait = Leases[Math.Min(attempt, Leases.Count) - 1];
                if (result.RetryAfter is { } retryAfter && retryAfter > wait)
                {
                    wait = retryAfter;
                }

                await queue.ScheduleRetryAsync(id, clock.UtcNow + wait, cancellationToken);
            }
        }

        return pending.Count;
    }
}
