using BuildingBlocks.Application;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Modules.Pipeline.Infrastructure.Messaging;

// Spec 2026-10-13 D6: cada 10 s. El sondeo es costo en reposo contra una base compartida y nadie lo espera de forma
// interactiva: el tablero refleja el cambio dentro del intervalo. Sólo el apagado del host lo detiene; un tick fallido se
// registra y el siguiente reintenta.
internal sealed partial class QuotationEventsWorker(IQueueDoorbell doorbell, QuotationEventsProcessor processor, ILogger<QuotationEventsWorker> logger) : BackgroundService
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    [LoggerMessage(Level = LogLevel.Error, Message = "Pipeline quotation events tick failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Spec 2026-11-13 (Valkey), D-7: espera el timbre de su cola hasta la red de seguridad; con el área apagada o
        // Valkey caído, su intervalo de hoy. Sólo el apagado sale del bucle; cualquier otra falla se loguea y sigue.
        using var listener = doorbell.Listen(DoorbellQueues.Outbox);
        await DoorbellPolling.RunAsync(
            listener,
            PollInterval,
            DoorbellQueues.SafetyNet(DoorbellQueues.Outbox),
            async _ =>
            {
                try
                {
                    await processor.ProcessBatchAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    LogTickFailed(logger, exception);
                }

                return false;
            },
            stoppingToken);
    }
}
