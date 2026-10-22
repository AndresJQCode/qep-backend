using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Automations.Application;
using Modules.Automations.Application.Engine;
using Modules.Automations.Infrastructure.Options;

namespace Modules.Automations.Infrastructure.Engine;

/// <summary>
/// Spec 2026-10-21 §5.2: vacía <c>messaging.automation_inbox</c>. Reclama un lote en un scope y procesa cada fila en el suyo con
/// <see cref="RunProcessor"/>, como <c>MediaCopyWorker</c> (Mensajería). Varias réplicas pueden correrlo a la vez: el reclamo da un solo
/// ganador por fila y sólo la más vieja pendiente de cada conversación, y el lock por conversación de la unidad serializa el resto.
/// Aparte, cada <c>AbandonSweepMinutes</c>, barre las corridas abandonadas (D-M13) y purga el inbox procesado (P2-R7), en su propio
/// bucle para que un barrido largo no frene las respuestas del bot.
/// </summary>
internal sealed partial class AutomationInboxWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<AutomationsWorkerOptions> options,
    ILogger<AutomationInboxWorker> logger) : BackgroundService
{
    /// <summary>Filas por <c>DELETE</c> de la purga del inbox, como <c>WebhookPurgeWorker.BatchSize</c>.</summary>
    internal const int PurgeBatchSize = 1000;

    [LoggerMessage(Level = LogLevel.Error, Message = "Automation inbox tick failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    // Sin texto del mensaje ni datos del cliente: sólo ids. La fila queda para su reintento por lease.
    [LoggerMessage(Level = LogLevel.Error, Message = "Automation unit for inbox row {MessageId} of tenant {TenantId} threw on attempt {Attempts}; it is retried when its lease expires.")]
    private static partial void LogUnitFailed(ILogger logger, Exception exception, Guid tenantId, Guid messageId, int attempts);

    [LoggerMessage(Level = LogLevel.Error, Message = "Automations sweep failed; it runs again on the next interval.")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Abandoning run {RunId} of tenant {TenantId} failed; the next sweep tries again.")]
    private static partial void LogAbandonFailed(ILogger logger, Exception exception, Guid tenantId, Guid runId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Automations sweep abandoned {Abandoned} runs and purged {Purged} processed inbox rows older than {Days} days.")]
    private static partial void LogSwept(ILogger logger, int abandoned, int purged, int days);

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(PollAsync(stoppingToken), SweepEveryIntervalAsync(stoppingToken));

    private async Task PollAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Value.InboxPollSeconds)));
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

    /// <summary>El primer barrido espera un intervalo: una corrida abandonada ya esperó 24 h y no hay apuro al arrancar, y las réplicas que
    /// arrancan juntas en un despliegue no barren todas a la vez.</summary>
    private async Task SweepEveryIntervalAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, options.Value.AbandonSweepMinutes)));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await SweepAsync(scope.ServiceProvider.GetRequiredService<IClock>().UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogSweepFailed(logger, exception);
            }
        }
    }

    /// <summary>Un barrido con la hora <paramref name="now"/>: abandona lo que espera hace más de 24 h y purga el inbox procesado.
    /// Internal para las pruebas.</summary>
    internal async Task SweepAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var abandoned = await AbandonAsync(now, cancellationToken);
        var days = Math.Max(1, options.Value.InboxRetentionDays);
        var purged = await PurgeInboxAsync(now.AddDays(-days), cancellationToken);
        LogSwept(logger, abandoned, purged, days);
    }

    /// <summary>
    /// D-M13. Cada candidata en su scope y su unidad: el tracker de una no arrastra a la siguiente, y la que falla (se registra) no frena a
    /// las demás. Sigue por lotes mientras un lote llega lleno y abandonó algo; una que falla siempre vuelve a salir en el lote siguiente,
    /// y sin la segunda condición el barrido no terminaría.
    /// </summary>
    private async Task<int> AbandonAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var total = 0;
        IReadOnlyList<AbandonedRun> candidates;
        int abandoned;
        do
        {
            await using (var scope = scopeFactory.CreateAsyncScope())
            {
                candidates = await scope.ServiceProvider.GetRequiredService<RunProcessor>().FindAbandonedAsync(now, cancellationToken);
            }

            abandoned = 0;
            foreach (var candidate in candidates)
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                try
                {
                    if (await scope.ServiceProvider.GetRequiredService<RunProcessor>().AbandonAsync(candidate, now, cancellationToken))
                    {
                        abandoned++;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    LogAbandonFailed(logger, exception, candidate.TenantId, candidate.RunId);
                }
            }

            total += abandoned;
        }
        while (candidates.Count == RunProcessor.AbandonBatchSize && abandoned > 0);

        return total;
    }

    /// <summary>P2-R7: lote por lote hasta que uno sale incompleto, como <c>WebhookPurgeWorker.DrainAsync</c>.</summary>
    private async Task<int> PurgeInboxAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        var total = 0;
        int deleted;
        do
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            deleted = await scope.ServiceProvider.GetRequiredService<IAutomationInboundQueue>().PurgeProcessedBeforeAsync(cutoff, PurgeBatchSize, cancellationToken);
            total += deleted;
        }
        while (deleted == PurgeBatchSize);

        return total;
    }

    /// <summary>
    /// Una pasada: hasta <c>InboxBatchSize</c> filas. Ruling P2-R24: se reclaman de a una, justo antes de procesarla, para que cada unidad
    /// empiece con su lease entero (30 s el primer intento). Reclamadas en lote, la vigésima esperaría a las diecinueve de antes con su lease
    /// corriendo, y otra réplica podría volver a tomarla a mitad de su unidad. Internal para las pruebas.
    /// </summary>
    internal async Task DrainAsync(CancellationToken cancellationToken)
    {
        for (var processed = 0; processed < Math.Max(1, options.Value.InboxBatchSize); processed++)
        {
            // Un scope por fila: el tracker de Automations y la transacción de la unidad no se comparten entre conversaciones.
            await using var scope = scopeFactory.CreateAsyncScope();
            var now = scope.ServiceProvider.GetRequiredService<IClock>().UtcNow;
            var claimed = await scope.ServiceProvider.GetRequiredService<IAutomationInboundQueue>().ClaimAsync(now, 1, cancellationToken);
            if (claimed.Count == 0)
            {
                return;
            }

            var item = claimed[0];
            try
            {
                await scope.ServiceProvider.GetRequiredService<RunProcessor>().ProcessAsync(item, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogUnitFailed(logger, exception, item.TenantId, item.MessageId, item.Attempts);
            }
        }
    }
}
