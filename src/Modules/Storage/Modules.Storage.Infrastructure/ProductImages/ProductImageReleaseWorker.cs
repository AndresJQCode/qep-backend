using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Modules.Storage.Infrastructure.ProductImages;

// Spec 2026-11-08. Cada 15 s, como los demás consumidores del outbox: nadie espera la purga de forma
// interactiva y el sondeo es costo en reposo contra una base compartida. El procesador corre en su propio
// scope para que el DbContext esté fresco.
internal sealed partial class ProductImageReleaseWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<ProductImageReleaseWorker> logger) : BackgroundService
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    [LoggerMessage(Level = LogLevel.Error, Message = "Product image release tick failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IProductImageReleaseProcessor>()
                    .ProcessPendingAsync(stoppingToken);
            }
            // Sólo el apagado del host detiene el worker. Otra cancelación (un timeout de R2 que llega
            // como TaskCanceledException) es un tick fallido más: se registra y el siguiente reintenta.
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
