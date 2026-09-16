using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Modules.Storage.Infrastructure.PaymentProofs;

// Spec 2026-09-16, D6 y D12: la reconciliación corre dentro de la API, cada IntervalHours. La primera
// corrida llega InitialDelay después de arrancar y no después de un intervalo completo: el temporizador
// no se persiste, así que con deploys o reinicios más seguidos que IntervalHours nunca correría, ni
// siquiera en seco, y los logs de "would delete" que el despliegue de D12 necesita revisar no
// aparecerían. La espera corta deja que el host termine de arrancar antes de recorrer el bucket.
//
// Varias réplicas pueden correrla a la vez: borrar un objeto que ya no existe no falla, y en producción
// DryRun arranca en true.
internal sealed partial class PaymentProofOrphanCleanupWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<StorageOptions> options,
    ILogger<PaymentProofOrphanCleanupWorker> logger) : BackgroundService
{
    internal static readonly TimeSpan DefaultInitialDelay = TimeSpan.FromMinutes(5);

    // Sólo las pruebas lo cambian; el contenedor usa el valor por defecto.
    internal TimeSpan InitialDelay { get; init; } = DefaultInitialDelay;

    // Sólo para pruebas: avisa que ExecuteAsync ya entró en la espera inicial, para detener el worker
    // sin carreras con el Task.Run de BackgroundService. En producción es null.
    internal Action? OnInitialDelayStarted { get; init; }

    [LoggerMessage(Level = LogLevel.Error, Message = "Payment proof orphan cleanup tick failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(
            TimeSpan.FromHours(options.Value.PaymentProofOrphanCleanup.IntervalHours));
        try
        {
            OnInitialDelayStarted?.Invoke();
            await Task.Delay(InitialDelay, stoppingToken);
            do
            {
                await RunOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        // El apagado del host, durante la espera inicial, entre corridas o en medio de una, termina el
        // worker sin error.
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var processor = scope.ServiceProvider.GetRequiredService<IPaymentProofOrphanCleanupProcessor>();
            await processor.CleanupAsync(stoppingToken);
        }
        // Sólo el apagado del host detiene el worker, como StagingCleanupWorker. Otra cancelación (un
        // timeout de R2 al listar, que llega como TaskCanceledException) es una corrida fallida más: se
        // registra y la siguiente reintenta.
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogTickFailed(logger, exception);
        }
    }
}
