using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Modules.Storage.Infrastructure.ChatbotMedia;

// Spec 2026-11-18 («Lifecycle»), plan Task 12. Consumidor de platform.outbox_messages: espera el timbre de DoorbellQueues.Outbox hasta la red
// de seguridad (backend CLAUDE.md), y con el área apagada o Valkey caído, cada 15 s como los demás consumidores del outbox: nadie espera la
// liberación de forma interactiva. El procesador corre en su propio scope para que el DbContext esté fresco. Sólo el apagado del host lo
// detiene; un tick fallido se registra y el siguiente reintenta.
internal sealed partial class ChatbotMediaReleaseWorker(
    IQueueDoorbell doorbell,
    IServiceScopeFactory scopeFactory,
    ILogger<ChatbotMediaReleaseWorker> logger) : BackgroundService
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    [LoggerMessage(Level = LogLevel.Error, Message = "Chatbot media release tick failed.")]
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
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var processed = await scope.ServiceProvider.GetRequiredService<IChatbotMediaReleaseProcessor>()
                        .ProcessPendingAsync(stoppingToken);
                    // Un lote lleno puede dejar más: se repite sin esperar (sólo con el timbre vivo, lo decide DoorbellPolling).
                    return processed >= ChatbotMediaReleaseProcessor.BatchSize;
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
