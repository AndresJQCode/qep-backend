using BuildingBlocks.Application;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Modules.Messaging.Infrastructure.Coexistence;

// Spec 2026-10-29: cada 10 s, como QuotationEventsWorker. El sondeo es costo en reposo y nadie lo espera de forma interactiva: la
// importación nace dentro del intervalo. Sólo el apagado del host lo detiene; un tick fallido se registra y el siguiente reintenta.
internal sealed partial class CoexistenceEventsWorker(IQueueDoorbell doorbell, CoexistenceEventsProcessor processor, ILogger<CoexistenceEventsWorker> logger) : BackgroundService
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    [LoggerMessage(Level = LogLevel.Error, Message = "Messaging coexistence events tick failed.")]
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
                    await DrainAsync(stoppingToken);
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

    /// <summary>Una pasada: internal para las pruebas.</summary>
    internal Task DrainAsync(CancellationToken cancellationToken) => processor.ProcessBatchAsync(cancellationToken);
}
