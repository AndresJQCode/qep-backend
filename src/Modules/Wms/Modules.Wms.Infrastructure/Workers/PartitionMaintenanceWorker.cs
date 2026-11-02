using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Wms.Application;

namespace Modules.Wms.Infrastructure.Workers;

/// <summary>
/// Mantiene creada la partición del mes en curso y la del siguiente, para que un movimiento nunca caiga en <c>DEFAULT</c> (RNF-08).
/// Si crearla falla —típicamente porque <c>DEFAULT</c> ya tiene filas de ese mes— lo registra y deja <c>qep.wms.partition.missing</c>
/// en 1, que es lo que alerta. <b>Sólo crea:</b> no hay camino de <c>DETACH</c> ni de <c>DROP</c>, porque una partición dentro de la
/// ventana de <c>occurredAt</c> (S12) o un dato borrado no se recuperan.
/// </summary>
internal sealed partial class PartitionMaintenanceWorker(
    IServiceScopeFactory scopeFactory,
    WmsMetrics metrics,
    IOptions<WmsWorkerOptions> options,
    ILogger<PartitionMaintenanceWorker> logger) : BackgroundService
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Wms partition maintenance tick failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Wms could not create the stock movement partition for {Month}.")]
    private static partial void LogEnsureFailed(ILogger logger, Exception exception, DateOnly month);

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        WorkerLoop.RunAsync(
            TimeSpan.FromSeconds(options.Value.WorkerStartupSeconds),
            TimeSpan.FromHours(options.Value.PartitionSweepHours),
            RunOnceAsync,
            exception => LogTickFailed(logger, exception),
            stoppingToken);

    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var partitions = scope.ServiceProvider.GetRequiredService<IPartitionMaintenance>();

        // UTC, como los límites de la función: el mes de "hoy" en otra zona partiría el mes en dos.
        var today = DateOnly.FromDateTime(scope.ServiceProvider.GetRequiredService<IClock>().UtcNow.UtcDateTime);
        var next = today.AddMonths(1);

        await TryEnsureAsync(partitions, today, cancellationToken);
        await TryEnsureAsync(partitions, next, cancellationToken);
        metrics.SetPartitionMissing(!await partitions.ExistsAsync(next, cancellationToken));
    }

    private async Task TryEnsureAsync(IPartitionMaintenance partitions, DateOnly month, CancellationToken cancellationToken)
    {
        try
        {
            await partitions.EnsureAsync(month, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Un mes que falla no impide intentar el otro ni medir el gauge.
            LogEnsureFailed(logger, exception, month);
        }
    }
}
