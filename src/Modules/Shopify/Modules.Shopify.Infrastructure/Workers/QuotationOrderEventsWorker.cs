using BuildingBlocks.Application;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Modules.Shopify.Infrastructure.Messaging;

namespace Modules.Shopify.Infrastructure.Workers;

// Spec 2026-11-24 §11.1.1: cada 10 s, como CitySynonymEventsWorker: acá lo espera quien cambió la dirección, y después todavía
// corre el worker de envíos. Sólo el apagado del host lo detiene; un tick fallido se registra y el siguiente reintenta.
internal sealed partial class QuotationOrderEventsWorker(IQueueDoorbell doorbell, QuotationOrderEventsProcessor processor, ILogger<QuotationOrderEventsWorker> logger) : BackgroundService
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    [LoggerMessage(Level = LogLevel.Error, Message = "Shopify quotation order events tick failed.")]
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
                    return await processor.ProcessBatchAsync(stoppingToken);
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
