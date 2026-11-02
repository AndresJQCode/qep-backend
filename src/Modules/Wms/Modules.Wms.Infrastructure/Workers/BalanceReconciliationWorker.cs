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

    [LoggerMessage(Level = LogLevel.Error, Message = "Wms balance reconciliation failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

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

                // Se marca antes: una conciliación que falla a medias no se reintenta cada 30 minutos de esa hora.
                _lastRunDay = today;
                await RunOnceAsync(token);
            },
            exception => LogTickFailed(logger, exception),
            stoppingToken);

    /// <summary>Recorre las bodegas de a una, tenant por tenant, y suma las claves con diferencia.</summary>
    internal async Task<long> RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var reconciliation = scope.ServiceProvider.GetRequiredService<IBalanceReconciliation>();

        long total = 0;
        foreach (var warehouseId in await reconciliation.ListWarehousesAsync(cancellationToken))
        {
            total += await reconciliation.ReconcileWarehouseAsync(warehouseId, cancellationToken);
        }

        metrics.SetBalanceDrift(total);
        if (total > 0)
        {
            LogDrift(logger, total);
        }

        return total;
    }
}
