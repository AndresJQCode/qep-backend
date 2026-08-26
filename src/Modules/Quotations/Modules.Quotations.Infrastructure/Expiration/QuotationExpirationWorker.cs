using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Modules.Quotations.Infrastructure.Expiration;

// Poller de fondo que corre el barrido de vencimiento (US-19) a intervalo configurable. Cada
// tick corre en su propio scope para que el DbContext scoped esté fresco -- mismo patrón que
// OutboxPublisherWorker en Tenancy y StagingCleanupWorker en Storage.
internal sealed partial class QuotationExpirationWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<QuotationsOptions> options,
    ILogger<QuotationExpirationWorker> logger) : BackgroundService
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Quotation expiration sweep tick failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(
            TimeSpan.FromMinutes(options.Value.ExpirationSweepMinutes));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<IQuotationExpirationProcessor>();
                await processor.ExpirePastDueQuotationsAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception)
            {
                LogTickFailed(logger, exception);
            }
        }
    }
}
