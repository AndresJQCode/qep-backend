using BuildingBlocks.Application;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Modules.Storage.Infrastructure.Messaging;

namespace Modules.Storage.Infrastructure.HrDocuments;

// Spec 2026-11-26 («Worker»). Consumidor de platform.outbox_messages: espera el timbre de DoorbellQueues.Outbox hasta la red de seguridad, y
// con el área apagada o Valkey caído, cada 15 s como los demás consumidores del outbox; sin PeriodicTimer, como ChatbotMediaReleaseWorker.
// El procesador es singleton y abre un scope por mensaje. Sólo el apagado del host lo detiene; un tick fallido se registra y el siguiente
// reintenta. Dos réplicas son seguras: cada mensaje se reclama (StorageInboxClaims) y cada archivo se bloquea con FOR UPDATE.
internal sealed partial class HrDocumentReleaseWorker(
    IQueueDoorbell doorbell,
    IHrDocumentReleaseProcessor processor,
    ILogger<HrDocumentReleaseWorker> logger) : BackgroundService
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    [LoggerMessage(Level = LogLevel.Error, Message = "HR document release tick failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var listener = doorbell.Listen(DoorbellQueues.Outbox);
        await DoorbellPolling.RunAsync(
            listener,
            PollInterval,
            DoorbellQueues.SafetyNet(DoorbellQueues.Outbox),
            async _ =>
            {
                try
                {
                    // Un lote lleno puede dejar más: se repite sin esperar (sólo con el timbre vivo, lo decide DoorbellPolling).
                    return await processor.ProcessPendingAsync(stoppingToken) >= ClaimedOutboxConsumer.BatchSize;
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
