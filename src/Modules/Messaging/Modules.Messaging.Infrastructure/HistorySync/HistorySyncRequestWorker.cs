using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Modules.Messaging.Infrastructure.Coexistence;

/// <summary>Spec 2026-10-29: cada cuánto sondea <see cref="HistorySyncRequestWorker"/>. No es configuración (el spec §6 no agrega
/// claves): es un singleton para que las pruebas lo reemplacen y el worker no compita con el <c>DrainAsync</c> que ellas llaman.</summary>
internal sealed record HistorySyncRequestSchedule(TimeSpan PollInterval)
{
    public static readonly HistorySyncRequestSchedule Default = new(TimeSpan.FromSeconds(10));
}

// Spec 2026-10-29: cada 10 s, como CoexistenceEventsWorker. Sólo el apagado del host lo detiene; un tick fallido se registra y el
// siguiente reintenta.
internal sealed partial class HistorySyncRequestWorker(
    HistorySyncRequestProcessor processor,
    HistorySyncRequestSchedule schedule,
    ILogger<HistorySyncRequestWorker> logger) : BackgroundService
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Messaging history sync request tick failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(schedule.PollInterval);
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

    /// <summary>Una pasada: internal para las pruebas.</summary>
    internal Task DrainAsync(CancellationToken cancellationToken) => processor.ProcessBatchAsync(cancellationToken);
}
