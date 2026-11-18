using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Modules.Storage.Infrastructure.TemplateImages;

// Spec 2026-11-17, «Closing the residual gaps»: el barrido diario de imágenes de encabezado sin plantilla. Mismo calendario que
// PaymentProofOrphanCleanupWorker: la primera corrida llega InitialDelay después de arrancar y no después de 24 h, porque el temporizador
// no se persiste y con deploys más seguidos que un día nunca correría. No hay cola que escuchar: nadie espera el barrido y ningún INSERT
// lo dispara, así que no usa timbre.
//
// Varias réplicas pueden correrlo a la vez: cada archivo se reclama con FOR UPDATE SKIP LOCKED y se relee bajo el candado
// (TemplateHeaderImageFiles), así que nadie lo suelta ni lo audita dos veces.
internal sealed partial class TemplateHeaderImageSweepWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<TemplateHeaderImageSweepWorker> logger) : BackgroundService
{
    internal static readonly TimeSpan DefaultInitialDelay = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    // Sólo las pruebas lo cambian; el contenedor usa el valor por defecto.
    internal TimeSpan InitialDelay { get; init; } = DefaultInitialDelay;

    [LoggerMessage(Level = LogLevel.Error, Message = "Template header image sweep failed.")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Template header image sweep released {Released} unreferenced images.")]
    private static partial void LogSweepFinished(ILogger logger, int released);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
            do
            {
                await RunOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        // El apagado del host, durante la espera inicial, entre corridas o en medio de una, termina el worker sin error.
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var clock = scope.ServiceProvider.GetRequiredService<IClock>();
            var released = await scope.ServiceProvider.GetRequiredService<ITemplateHeaderImageSweep>()
                .SweepAsync(clock.UtcNow, stoppingToken);
            LogSweepFinished(logger, released);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        // Sólo el apagado del host detiene el worker: cualquier otro fallo (la base, un timeout) es una corrida perdida y mañana reintenta.
        catch (Exception exception)
        {
            LogSweepFailed(logger, exception);
        }
    }
}
