using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Messaging.Infrastructure.Options;
using Modules.Messaging.Infrastructure.Persistence;

namespace Modules.Messaging.Infrastructure.Webhook;

/// <summary>§8.2: borra en lotes las entregas procesadas con más de <c>DeliveryRetentionDays</c>, cada
/// <c>PurgeIntervalHours</c>. Lo que agotó sus intentos quedó procesado y también se purga.</summary>
internal sealed partial class WebhookPurgeWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<MessagingWorkerOptions> options,
    ILogger<WebhookPurgeWorker> logger) : BackgroundService
{
    internal const int BatchSize = 1000;

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook purge deleted {Deleted} processed deliveries older than {Days} days.")]
    private static partial void LogPurged(ILogger logger, int deleted, int days);

    [LoggerMessage(Level = LogLevel.Error, Message = "Webhook purge failed; it runs again on the next interval.")]
    private static partial void LogFailed(ILogger logger, Exception exception);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(Math.Max(1, options.Value.PurgeIntervalHours)));
        do
        {
            try
            {
                await Task.Yield();
                await DrainAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Una pasada completa, lote por lote hasta que un lote sale incompleto: internal para las pruebas (P10).</summary>
    internal async Task DrainAsync(CancellationToken cancellationToken)
    {
        var days = Math.Max(1, options.Value.DeliveryRetentionDays);
        var total = 0;
        int deleted;
        do
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
            var cutoff = scope.ServiceProvider.GetRequiredService<IClock>().UtcNow.AddDays(-days);
            deleted = await dbContext.Database.ExecuteSqlAsync(
                $"""
                DELETE FROM messaging.webhook_deliveries
                WHERE id IN (SELECT id FROM messaging.webhook_deliveries WHERE processed_at < {cutoff} ORDER BY id LIMIT {BatchSize})
                """, cancellationToken);
            total += deleted;
        }
        while (deleted == BatchSize);

        LogPurged(logger, total, days);
    }
}
