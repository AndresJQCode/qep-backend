using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Modules.Tenancy.Infrastructure.Messaging;

// Poller de fondo que drena el Outbox a intervalo fijo. Cada tick corre en su
// propio scope para que el DbContext scoped y los handlers estén frescos. El sondeo es costo en
// reposo contra una base compartida y nadie espera este worker de forma interactiva.
internal sealed partial class OutboxPublisherWorker(
    IQueueDoorbell doorbell,
    IServiceScopeFactory scopeFactory,
    ILogger<OutboxPublisherWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox processing tick failed.")]
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
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var processor = scope.ServiceProvider.GetRequiredService<IOutboxProcessor>();
                    return await processor.ProcessPendingAsync(stoppingToken) == OutboxProcessor.BatchSize;
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
