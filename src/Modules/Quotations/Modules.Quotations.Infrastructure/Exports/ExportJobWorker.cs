using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Modules.Quotations.Application;

namespace Modules.Quotations.Infrastructure.Exports;

// D7: un worker, un job a la vez. Mismo esqueleto que los workers de Notifications —PeriodicTimer,
// scope nuevo por unidad de trabajo, una falla se loguea y no mata el loop—. Concurrencia 1 a
// propósito: el Excel se arma en el mismo pod de 1Gi que atiende la API.
internal sealed partial class ExportJobWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<ExportJobWorker> logger) : BackgroundService
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan PurgeInterval = TimeSpan.FromDays(1);

    // En memoria y no en la base: con una réplica, que un reinicio adelante la purga no cuesta
    // nada —borrar lo vencido dos veces el mismo día es inofensivo—.
    private DateTimeOffset _nextPurgeAt = DateTimeOffset.MinValue;

    [LoggerMessage(Level = LogLevel.Error, Message = "Export job tick failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Export job lease was lost before it could be completed; another worker owns it.")]
    private static partial void LogLeaseLost(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Purged {Count} finished export jobs.")]
    private static partial void LogPurged(ILogger logger, int count);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try
            {
                await DrainAsync(stoppingToken);
                await PurgeIfDueAsync(stoppingToken);
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

    // Uno por uno hasta vaciar lo vencido, cada job en su scope: un DbContext por job no arrastra
    // entidades trackeadas de un export al siguiente. No hay loop infinito posible: un reintento
    // queda con next_attempt_at en el futuro y un lease perdido es de otro worker.
    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var outcome = await scope.ServiceProvider.GetRequiredService<ExportJobRunner>()
                .RunNextAsync(cancellationToken);

            if (outcome == ExportJobRunOutcome.NoJob)
            {
                return;
            }

            if (outcome == ExportJobRunOutcome.LeaseLost)
            {
                LogLeaseLost(logger);
            }
        }
    }

    private async Task PurgeIfDueAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (now < _nextPurgeAt)
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var purged = await scope.ServiceProvider.GetRequiredService<ExportJobRunner>()
            .PurgeFinishedAsync(cancellationToken);
        _nextPurgeAt = now.Add(PurgeInterval);
        LogPurged(logger, purged);
    }
}
