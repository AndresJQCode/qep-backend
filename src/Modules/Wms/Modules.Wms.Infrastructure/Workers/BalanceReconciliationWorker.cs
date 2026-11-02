using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Wms.Application;

namespace Modules.Wms.Infrastructure.Workers;

/// <summary>
/// La conciliación nocturna del ledger contra los saldos (RF-029, RNF-01). Despierta cada <c>ReconciliationSweepMinutes</c> y corre si
/// es la hora <c>ReconciliationHourUtc</c> y hoy no corrió; la marca es en memoria, así que un reinicio dentro de esa hora la repite
/// (se registran las diferencias de nuevo, no se corrige nada). Publica el total en <c>qep.wms.balance.drift</c>, que alerta.
/// </summary>
internal sealed partial class BalanceReconciliationWorker(
    IServiceScopeFactory scopeFactory,
    WmsMetrics metrics,
    IOptions<WmsWorkerOptions> options,
    ILogger<BalanceReconciliationWorker> logger) : BackgroundService
{
    private DateOnly _lastRunDay;
    private bool _lastRunFailed;

    [LoggerMessage(Level = LogLevel.Error, Message = "Wms balance reconciliation failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Wms reconciliation could not check warehouse {WarehouseId}.")]
    private static partial void LogWarehouseFailed(ILogger logger, Exception exception, Guid warehouseId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Wms reconciliation found {Drift} balance keys that differ from the ledger.")]
    private static partial void LogDrift(ILogger logger, long drift);

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        WorkerLoop.RunAsync(
            TimeSpan.FromSeconds(options.Value.WorkerStartupSeconds),
            TimeSpan.FromMinutes(options.Value.ReconciliationSweepMinutes),
            async token =>
            {
                // El reloj es scoped en el composition root: se resuelve por pasada, no en el constructor del singleton.
                await using var scope = scopeFactory.CreateAsyncScope();
                var now = scope.ServiceProvider.GetRequiredService<IClock>().UtcNow;
                var today = DateOnly.FromDateTime(now.UtcDateTime);
                if (now.Hour != options.Value.ReconciliationHourUtc || _lastRunDay == today)
                {
                    return;
                }

                // Sólo una pasada limpia cierra el día: si alguna bodega falló, el siguiente barrido de esa hora reintenta (las
                // diferencias ya registradas hoy no se duplican).
                await RunOnceAsync(token);
                if (!_lastRunFailed)
                {
                    _lastRunDay = today;
                }
            },
            exception => LogTickFailed(logger, exception),
            stoppingToken);

    /// <summary>Recorre las bodegas de a una, tenant por tenant, y suma las claves con diferencia.</summary>
    internal async Task<long> RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var reconciliation = scope.ServiceProvider.GetRequiredService<IBalanceReconciliation>();

        long total = 0;
        long failed = 0;
        foreach (var warehouseId in await reconciliation.ListWarehousesAsync(cancellationToken))
        {
            try
            {
                total += await reconciliation.ReconcileWarehouseAsync(warehouseId, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Una bodega que falla no deja sin revisar a las demás: se cuenta y se sigue.
                failed++;
                LogWarehouseFailed(logger, exception, warehouseId);
            }
        }

        // El gauge de diferencia sólo cuenta lo revisado; lo que no se pudo revisar sale por su propio gauge, para que un 0 aquí no
        // se lea como «todo cuadra» cuando hubo bodegas sin revisar.
        _lastRunFailed = failed > 0;
        metrics.SetBalanceDrift(total);
        metrics.SetReconciliationFailed(failed);
        if (total > 0)
        {
            LogDrift(logger, total);
        }

        return total;
    }
}
