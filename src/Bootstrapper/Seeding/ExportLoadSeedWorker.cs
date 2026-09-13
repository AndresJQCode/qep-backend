using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bootstrapper.Seeding;

/// <summary>
/// Corre la carga sintética de la exportación (spec 2026-09-13, A10) cuando
/// <c>Seed:ExportLoad:Quotations</c> es mayor que 0.
///
/// Arranca después de que la API está en pie, nunca dentro de RunQepSeedAsync. El startupProbe le da
/// al pod 60 s como máximo (prod-deployment.yaml: 12 intentos cada 5 s), y sembrar decenas de miles de
/// filas dentro del arranque haría que Kubernetes lo matara. Con maxUnavailable: 0, además, el deploy
/// nunca quedaría listo.
///
/// Público y no internal para que las pruebas lo encuentren entre los IHostedService y esperen su
/// ExecuteTask: Bootstrapper no le abre sus internals a nadie.
/// </summary>
public sealed partial class ExportLoadSeedWorker(
    IServiceProvider services,
    IOptions<SeedOptions> options,
    IHostApplicationLifetime lifetime,
    ILogger<ExportLoadSeedWorker> logger) : BackgroundService
{
    // Ruidoso a propósito, como la semilla de arranque: concede admin, y prendido tiene que verse.
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Export load seed is ENABLED: seeding {Quotations} quotations into tenant '{TenantSlug}' and granting the admin role to '{OwnerEmail}'. Set Seed:ExportLoad:Quotations back to 0 after measuring.")]
    private static partial void LogStarting(ILogger logger, int quotations, string tenantSlug, string ownerEmail);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Export load seed finished: {Customers} customers, {Quotations} quotations, {Items} items and {Sales} sales in {ElapsedMilliseconds} ms.")]
    private static partial void LogFinished(
        ILogger logger, int customers, int quotations, int items, int sales, long elapsedMilliseconds);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Export load seed skipped: tenant '{TenantSlug}' already has quotations.")]
    private static partial void LogSkipped(ILogger logger, string tenantSlug);

    [LoggerMessage(Level = LogLevel.Error, Message = "Export load seed failed; nothing was committed.")]
    private static partial void LogFailed(ILogger logger, Exception exception);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var quotations = options.Value.ExportLoad.Quotations;
        if (quotations <= 0)
        {
            return;
        }

        if (!await WaitForStartedAsync(stoppingToken))
        {
            return;
        }

        var ownerEmail = options.Value.OwnerEmail!;
        LogStarting(logger, quotations, ExportLoadSeeder.TenantSlug, ownerEmail);
        try
        {
            var result = await services.SeedExportLoadAsync(ownerEmail, quotations, stoppingToken);
            if (!result.Seeded)
            {
                LogSkipped(logger, ExportLoadSeeder.TenantSlug);
                return;
            }

            // Variables locales y no expresiones en la llamada: CA1873 marca los argumentos que se
            // evalúan aunque el nivel esté apagado.
            var customers = result.Customers;
            var seededQuotations = result.Quotations;
            var items = result.Items;
            var sales = result.Sales;
            var elapsedMilliseconds = (long)result.Duration.TotalMilliseconds;
            LogFinished(logger, customers, seededQuotations, items, sales, elapsedMilliseconds);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Apagado a mitad de la siembra: la transacción no se commiteó, y el próximo arranque
            // siembra de cero.
        }
        catch (Exception exception)
        {
            // Nunca tumba el host: una excepción que sale de un BackgroundService detiene la aplicación
            // (BackgroundServiceExceptionBehavior.StopHost), y la API no puede caerse por una carga de
            // prueba.
            LogFailed(logger, exception);
        }
    }

    private async Task<bool> WaitForStartedAsync(CancellationToken stoppingToken)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Si la aplicación ya arrancó, Register invoca el callback en el acto.
        using var registration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        try
        {
            await started.Task.WaitAsync(stoppingToken);
            return true;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
