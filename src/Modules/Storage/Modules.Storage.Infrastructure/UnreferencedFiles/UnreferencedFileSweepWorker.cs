using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Modules.Storage.Domain;

namespace Modules.Storage.Infrastructure.UnreferencedFiles;

// Spec 2026-11-17, «Closing the residual gaps», y 2026-11-18 («Lifecycle»): el barrido diario de archivos de sistema que ya nadie usa
// (imágenes de encabezado de plantilla, archivos de chatbot). Mismo calendario que PaymentProofOrphanCleanupWorker: la primera corrida
// llega InitialDelay después de arrancar y no después de 24 h, porque el temporizador no se persiste y con deploys más seguidos que un día
// nunca correría. No hay cola que escuchar: nadie espera el barrido y ningún INSERT lo dispara, así que no usa timbre.
//
// Corre cada IUnreferencedFileSweep en su propio try: uno que falla (el módulo dueño caído) no deja sin correr a los demás.
//
// Varias réplicas pueden correrlo a la vez: cada archivo se reclama con FOR UPDATE SKIP LOCKED y se relee bajo el candado
// (UnreferencedFileCandidates), así que nadie lo suelta ni lo audita dos veces.
internal sealed partial class UnreferencedFileSweepWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<UnreferencedFileSweepWorker> logger) : BackgroundService
{
    internal static readonly TimeSpan DefaultInitialDelay = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    // Sólo las pruebas lo cambian; el contenedor usa el valor por defecto.
    internal TimeSpan InitialDelay { get; init; } = DefaultInitialDelay;

    [LoggerMessage(Level = LogLevel.Error, Message = "Unreferenced {OwnerType} sweep failed.")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception, FileOwnerType ownerType);

    [LoggerMessage(Level = LogLevel.Information, Message = "Unreferenced {OwnerType} sweep released {Released} files.")]
    private static partial void LogSweepFinished(ILogger logger, FileOwnerType ownerType, int released);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unreferenced file sweeps could not start.")]
    private static partial void LogRunFailed(ILogger logger, Exception exception);

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
            foreach (var sweep in scope.ServiceProvider.GetServices<IUnreferencedFileSweep>())
            {
                await RunSweepAsync(sweep, clock.UtcNow, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        // Sólo el apagado del host detiene el worker: cualquier otro fallo (armar el scope) es una corrida perdida y mañana reintenta.
        catch (Exception exception)
        {
            LogRunFailed(logger, exception);
        }
    }

    private async Task RunSweepAsync(IUnreferencedFileSweep sweep, DateTimeOffset now, CancellationToken stoppingToken)
    {
        try
        {
            var released = await sweep.SweepAsync(now, stoppingToken);
            LogSweepFinished(logger, sweep.OwnerType, released);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogSweepFailed(logger, exception, sweep.OwnerType);
        }
    }
}
