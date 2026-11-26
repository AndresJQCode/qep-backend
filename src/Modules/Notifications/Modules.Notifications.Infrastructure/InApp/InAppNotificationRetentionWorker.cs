using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Notifications.Infrastructure.Persistence;

namespace Modules.Notifications.Infrastructure.InApp;

/// <summary>
/// Spec 2026-11-22 D-11 and D-12: once an hour it deletes the bell entries older than <c>Notifications:InApp:RetentionDays</c> (90 by
/// default), in batches of <see cref="BatchSize"/> until none is left, and it takes the export link out of the entries that already
/// expired, which cannot be served again (the entry then says «El enlace venció»). Both are idempotent, so every replica can run
/// them: a <c>DELETE</c> by cutoff and an <c>UPDATE</c> that only touches what still holds a link.
/// </summary>
internal sealed partial class InAppNotificationRetentionWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<InAppNotificationRetentionWorker> logger) : BackgroundService
{
    internal const int BatchSize = 1000;

    internal static readonly TimeSpan Period = TimeSpan.FromHours(1);

    [LoggerMessage(Level = LogLevel.Information, Message = "In-app notification retention deleted {Deleted} entries older than {Days} days.")]
    private static partial void LogPurged(ILogger logger, int deleted, int days);

    [LoggerMessage(Level = LogLevel.Error, Message = "In-app notification retention failed; it runs again on the next period.")]
    private static partial void LogFailed(ILogger logger, Exception exception);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Period);
        do
        {
            try
            {
                await Task.Yield();
                await using var scope = scopeFactory.CreateAsyncScope();
                var now = scope.ServiceProvider.GetRequiredService<IClock>().UtcNow;
                await PurgeAsync(now, stoppingToken);
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

    /// <returns>How many entries were deleted. Internal for the tests.</returns>
    internal async Task<int> PurgeAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        var days = scope.ServiceProvider.GetRequiredService<IOptions<NotificationsOptions>>().Value.InApp.RetentionDays;
        var cutoff = now.AddDays(-days).ToUniversalTime();
        var universalNow = now.ToUniversalTime();

        var total = 0;
        int deleted;
        do
        {
            deleted = await dbContext.Database.ExecuteSqlAsync(
                $"""
                DELETE FROM notifications.in_app_notifications
                WHERE id IN (
                    SELECT id FROM notifications.in_app_notifications WHERE created_at < {cutoff} ORDER BY created_at LIMIT {BatchSize})
                """,
                cancellationToken);
            total += deleted;
        }
        while (deleted == BatchSize);

        // The link of an expired export is never served again, so it should not stay in the row for the rest of the retention.
        await dbContext.Database.ExecuteSqlAsync(
            $"""
            UPDATE notifications.in_app_notifications
            SET data = jsonb_set(data, ARRAY['downloadUrl'], 'null'::jsonb)
            WHERE kind = 'ExportReady'
              AND jsonb_typeof(data -> 'downloadUrl') = 'string'
              AND (data ->> 'expiresAt')::timestamptz < {universalNow}
            """,
            cancellationToken);

        LogPurged(logger, total, days);
        return total;
    }
}
