using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Wms.Application;
using Modules.Wms.Infrastructure.Persistence;

namespace Modules.Wms.Infrastructure.Workers;

public sealed record HousekeepingResult(int Receipts, int Previews, int Balances);

/// <summary>
/// Limpieza diaria de lo que el uso normal deja atrás: recibos de idempotencia pasados de <c>ReceiptRetentionDays</c>, previews de
/// carga inicial vencidos y saldos en cero (<c>on_hand = 0 AND reserved = 0</c>). Podar un saldo en cero no pierde nada: el ledger lo
/// vuelve a crear en el siguiente movimiento y la historia vive en <c>stock_movements</c>. La prueba llama <see cref="RunOnceAsync"/>
/// a mano; el temporizador sólo existe en producción.
/// </summary>
internal sealed partial class WmsHousekeepingWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<WmsWorkerOptions> options,
    ILogger<WmsHousekeepingWorker> logger) : BackgroundService
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Wms housekeeping tick failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Wms housekeeping removed {Receipts} receipts, {Previews} previews and {Balances} empty balances.")]
    private static partial void LogCompleted(ILogger logger, int receipts, int previews, int balances);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(options.Value.HousekeepingStartupSeconds), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // Primera pasada ya, luego cada HousekeepingHours: RunOnceAsync es idempotente.
        using var timer = new PeriodicTimer(TimeSpan.FromHours(options.Value.HousekeepingHours));
        do
        {
            try
            {
                var result = await RunOnceAsync(stoppingToken);
                LogCompleted(logger, result.Receipts, result.Previews, result.Balances);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception)
            {
                LogTickFailed(logger, exception);
            }
        }
        while (await WaitNextAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitNextAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public async Task<HousekeepingResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var now = DateTimeOffset.UtcNow;

        var receipts = await scope.ServiceProvider.GetRequiredService<IOperationReceipts>()
            .PurgeOlderThanAsync(now.AddDays(-options.Value.ReceiptRetentionDays), cancellationToken);

        // El preview vence por su propia columna: PreviewRetentionMinutes sólo se usa al crearlo.
        var previews = await scope.ServiceProvider.GetRequiredService<IImportPreviewStore>().PurgeExpiredAsync(now, cancellationToken);
        var dbContext = scope.ServiceProvider.GetRequiredService<WmsDbContext>();
        var balances = await dbContext.Database.ExecuteSqlAsync(
            $"DELETE FROM wms.stock_balances WHERE on_hand = 0 AND reserved = 0", cancellationToken);

        return new HousekeepingResult(receipts, previews, balances);
    }
}
