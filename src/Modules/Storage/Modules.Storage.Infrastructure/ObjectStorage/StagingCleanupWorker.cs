using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Modules.Storage.Infrastructure.ObjectStorage;

// Temporizador del barrido de staging/. La lógica vive en StagingCleanupProcessor (spec 2026-09-16,
// D11), que las pruebas de integración invocan directo. Cada tick corre en su propio scope para que
// el DbContext esté fresco.
internal sealed partial class StagingCleanupWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<StorageOptions> options,
    ILogger<StagingCleanupWorker> logger) : BackgroundService
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Storage staging cleanup tick failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(
            TimeSpan.FromMinutes(options.Value.StagingCleanupMinutes));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<IStagingCleanupProcessor>();
                await processor.CleanupAsync(stoppingToken);
            }
            // Sólo el apagado del host detiene el worker, como PaymentProofMoveWorker. Otra cancelación
            // (un timeout de R2 que llega como TaskCanceledException) es un tick fallido más: se
            // registra y el siguiente reintenta.
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
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
