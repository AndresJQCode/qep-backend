using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Modules.Storage.Infrastructure.PaymentProofs;

// Spec 2026-09-16, D6 y D12: la reconciliación corre dentro de la API, cada IntervalHours. El primer
// tick llega después de un intervalo completo, como StagingCleanupWorker: un reinicio de pod no
// dispara un recorrido del bucket, y ningún host de pruebas sale a R2.
internal sealed partial class PaymentProofOrphanCleanupWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<StorageOptions> options,
    ILogger<PaymentProofOrphanCleanupWorker> logger) : BackgroundService
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Payment proof orphan cleanup tick failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(
            TimeSpan.FromHours(options.Value.PaymentProofOrphanCleanup.IntervalHours));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<IPaymentProofOrphanCleanupProcessor>();
                await processor.CleanupAsync(stoppingToken);
            }
            // Sólo el apagado del host detiene el worker, como StagingCleanupWorker. Otra cancelación
            // (un timeout de R2 al listar, que llega como TaskCanceledException) es un tick fallido más:
            // se registra y el siguiente reintenta.
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
