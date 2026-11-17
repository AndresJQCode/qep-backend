using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Modules.Messaging.Infrastructure.CustomerDeletion;

// Spec 2026-11-10 (borrar clientes) A-9: cada 15 s. Nadie espera el borrado de R2 de forma interactiva, y el sondeo es costo en reposo.
// Sólo el apagado del host lo detiene; un tick fallido se registra y el siguiente reintenta.
internal sealed partial class CustomerMediaPurgeWorker(ICustomerMediaPurgeProcessor processor, ILogger<CustomerMediaPurgeWorker> logger) : BackgroundService
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    [LoggerMessage(Level = LogLevel.Error, Message = "Messaging customer media purge tick failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try
            {
                await processor.ProcessPendingAsync(stoppingToken);
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
}
