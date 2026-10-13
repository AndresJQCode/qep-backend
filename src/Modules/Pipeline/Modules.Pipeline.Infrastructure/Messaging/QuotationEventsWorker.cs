using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Modules.Pipeline.Infrastructure.Messaging;

// Spec 2026-10-13 D6: cada 3 s, como los demás consumidores del outbox. Sólo el apagado del host lo detiene; un tick fallido se
// registra y el siguiente reintenta.
internal sealed partial class QuotationEventsWorker(QuotationEventsProcessor processor, ILogger<QuotationEventsWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    [LoggerMessage(Level = LogLevel.Error, Message = "Pipeline quotation events tick failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try
            {
                await processor.ProcessBatchAsync(stoppingToken);
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
