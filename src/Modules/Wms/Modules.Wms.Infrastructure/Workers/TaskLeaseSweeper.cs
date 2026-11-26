using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Wms.Application;

namespace Modules.Wms.Infrastructure.Workers;

/// <summary>
/// Devuelve al pool (o a su asignado) las tareas en curso con el lease vencido (spec 2026-11-26 §6.7). Cada lote es una transacción
/// que toma las filas con <c>FOR UPDATE SKIP LOCKED</c> y aplica <c>WarehouseTask.ExpireLease</c>, así que dos réplicas se reparten
/// las tareas y ninguna vence dos veces.
/// </summary>
internal sealed partial class TaskLeaseSweeper(
    IServiceScopeFactory scopeFactory,
    IOptions<WmsWorkerOptions> options,
    ILogger<TaskLeaseSweeper> logger) : BackgroundService
{
    private const int BatchSize = 100;

    [LoggerMessage(Level = LogLevel.Error, Message = "Wms task lease sweep failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        WorkerLoop.RunAsync(
            TimeSpan.FromSeconds(options.Value.WorkerStartupSeconds),
            TimeSpan.FromSeconds(options.Value.TaskLeaseSweepSeconds),
            async token =>
            {
                // El reloj es scoped en el composition root: se resuelve por pasada, no en el constructor del singleton.
                await using var scope = scopeFactory.CreateAsyncScope();
                await RunOnceAsync(scope.ServiceProvider.GetRequiredService<IClock>().UtcNow, token);
            },
            exception => LogTickFailed(logger, exception),
            stoppingToken);

    /// <summary>Una pasada, lote por lote, hasta que no queda ninguna vencida a la hora <paramref name="now"/>. Devuelve cuántas
    /// tareas venció.</summary>
    public async Task<int> RunOnceAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var total = 0;
        int expired;
        do
        {
            expired = await SweepBatchAsync(now, cancellationToken);
            total += expired;
        }
        while (expired > 0);

        return total;
    }

    // Un scope por lote: nada queda rastreado de uno a otro. Si algo falla, la transacción se deshace y las tareas siguen vencidas
    // para la pasada siguiente.
    private async Task<int> SweepBatchAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IWmsUnitOfWork>();
        var tasks = scope.ServiceProvider.GetRequiredService<IWarehouseTaskRepository>();

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var expired = 0;
        foreach (var lease in await tasks.LockExpiredAsync(now, BatchSize, cancellationToken))
        {
            var task = await tasks.GetAsync(lease.TenantId, lease.TaskId, cancellationToken);
            if (task?.ExpireLease(now) == true)
            {
                expired++;
            }
        }

        if (expired > 0)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return expired;
    }
}
