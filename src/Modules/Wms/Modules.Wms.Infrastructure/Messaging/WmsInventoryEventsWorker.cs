using BuildingBlocks.Application;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Wms.Application;

namespace Modules.Wms.Infrastructure.Messaging;

// Inventario básico: copia de WholesaleOrderEventsWorker de Purchases. Espera el timbre del outbox hasta la red de seguridad; con el
// área apagada o Valkey caído, cada 10 s. Sólo el apagado del host lo detiene; un tick fallido se registra y el siguiente reintenta.
// Antes de la primera pasada espera WorkerStartupSeconds, como los demás workers de Bodegas: las pruebas lo fijan enorme y llaman
// al procesador a mano.
internal sealed partial class WmsInventoryEventsWorker(
    IQueueDoorbell doorbell,
    WmsInventoryEventsProcessor processor,
    IOptions<WmsWorkerOptions> options,
    ILogger<WmsInventoryEventsWorker> logger) : BackgroundService
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    [LoggerMessage(Level = LogLevel.Error, Message = "Wms inventory events tick failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(options.Value.WorkerStartupSeconds), stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

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
