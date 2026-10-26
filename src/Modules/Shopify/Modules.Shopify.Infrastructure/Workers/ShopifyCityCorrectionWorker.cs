using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Modules.Shopify.Application;
using Modules.Shopify.Domain;

namespace Modules.Shopify.Infrastructure.Workers;

/// <summary>
/// Spec 2026-10-26 D-C6, copia de <see cref="ShopifyWebhookDeliveryWorker"/>: toma lotes de <see cref="BatchSize"/> trabajos de
/// corrección, reclama cada uno con un <c>UPDATE</c> que se commitea solo (un solo ganador entre réplicas) y lo procesa en su propio
/// scope, así que un pedido que falla no frena a los demás. Lo que el procesador cierra (<c>Applied</c>, <c>Skipped</c>) lo guarda
/// él. Lo que lanza deja el reclamo vivo y vuelve al vencer el lease, que crece por intento; al <see cref="MaxAttempts"/> el
/// trabajo termina <c>Failed</c> con el tipo y el mensaje del error, y el operador lo puede reintentar desde la consola.
/// </summary>
internal sealed partial class ShopifyCityCorrectionWorker(
    IServiceScopeFactory scopeFactory, ILogger<ShopifyCityCorrectionWorker> logger) : BackgroundService
{
    internal const int BatchSize = 50;

    internal const int MaxAttempts = 5;

    /// <summary>Tope de lotes llenos seguidos en una pasada (1000 trabajos): el resto espera al tick siguiente.</summary>
    internal const int MaxFullBatchesPerTick = 20;

    /// <summary>Cada 10 s, como el consumidor que siembra (<see cref="CitySynonymEventsWorker.PollInterval"/>): la consola del
    /// operador pide el primer avance de un sinónimo nuevo cada 60 s, y entre el clic y ese avance corren el outbox, el consumidor y
    /// este worker. No subirlo sin mirar ese presupuesto.</summary>
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    /// <summary>La espera después del intento n es <c>Leases[n - 1]</c> (≈ 51 min hasta el quinto).</summary>
    internal static readonly IReadOnlyList<TimeSpan> Leases =
    [
        TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30),
    ];

    [LoggerMessage(Level = LogLevel.Error, Message = "Shopify city correction tick failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shopify city correction job {JobId} failed on attempt {Attempt} of {MaxAttempts}; it is retried in {Lease}.")]
    private static partial void LogAttemptFailed(ILogger logger, Exception exception, Guid jobId, int attempt, int maxAttempts, TimeSpan lease);

    [LoggerMessage(Level = LogLevel.Error, Message = "Shopify city correction job {JobId} failed on its last attempt ({Attempt}) and is closed as Failed: {Reason}.")]
    private static partial void LogGaveUp(ILogger logger, Exception exception, Guid jobId, int attempt, string reason);

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
            pending = await scope.ServiceProvider.GetRequiredService<IShopifyCityCorrectionJobQueue>()
                .ListPendingAsync(now, BatchSize, cancellationToken);
        }

        foreach (var id in pending)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var queue = services.GetRequiredService<IShopifyCityCorrectionJobQueue>();
            var clock = services.GetRequiredService<IClock>();
            if (await queue.TryClaimAsync(id, clock.UtcNow, cancellationToken) is not { } attempt)
            {
                continue; // Otra réplica lo tiene, o ya terminó desde que se armó el lote.
            }

            try
            {
                var job = await services.GetRequiredService<IShopifyCityCorrectionJobRepository>().FindAsync(id, cancellationToken);
                if (job is { Status: ShopifyCityCorrectionJobStatus.Pending })
                {
                    await services.GetRequiredService<ShopifyCityCorrectionProcessor>().ProcessAsync(job, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                if (attempt < MaxAttempts)
                {
                    // El reclamo queda vivo: vuelve al vencer el lease de este intento.
                    LogAttemptFailed(logger, exception, id, attempt, MaxAttempts, Leases[attempt - 1]);
                    continue;
                }

                // La causa de fondo y no la que la envuelve: un DbUpdateException dice poco, su PostgresException dice qué pasó.
                var cause = exception.GetBaseException();
                var lastError = Truncate($"{cause.GetType().Name}: {cause.Message}");
                LogGaveUp(logger, exception, id, attempt, lastError);
                await queue.MarkFailedAsync(id, clock.UtcNow, lastError, cancellationToken);
            }
        }

        return pending.Count;
    }

    private static string Truncate(string value) =>
        value.Length <= ShopifyCityCorrectionJob.LastErrorMaxLength ? value : value[..ShopifyCityCorrectionJob.LastErrorMaxLength];
}
