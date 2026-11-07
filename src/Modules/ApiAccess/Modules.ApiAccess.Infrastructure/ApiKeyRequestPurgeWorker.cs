using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Modules.ApiAccess.Domain;
using Modules.ApiAccess.Infrastructure.Persistence;

namespace Modules.ApiAccess.Infrastructure;

/// <summary>Borra en lotes las llamadas de la API externa con más de
/// <see cref="ApiKeyRequest.RetentionDays"/> días, una vez al día.</summary>
internal sealed partial class ApiKeyRequestPurgeWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<ApiKeyRequestPurgeWorker> logger) : BackgroundService
{
    internal const int BatchSize = 1000;
    internal const int IntervalHours = 24;

    [LoggerMessage(Level = LogLevel.Information, Message = "API request log purge deleted {Deleted} rows older than {Days} days.")]
    private static partial void LogPurged(ILogger logger, int deleted, int days);

    [LoggerMessage(Level = LogLevel.Error, Message = "API request log purge failed; it runs again on the next interval.")]
    private static partial void LogFailed(ILogger logger, Exception exception);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(IntervalHours));
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

    /// <summary>Una pasada completa, lote por lote hasta que un lote sale incompleto: internal para las pruebas.</summary>
    internal async Task DrainAsync(CancellationToken cancellationToken)
    {
        var total = 0;
        int deleted;
        do
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ApiAccessDbContext>();
            var cutoff = scope.ServiceProvider.GetRequiredService<IClock>().UtcNow
                .AddDays(-ApiKeyRequest.RetentionDays);
            deleted = await dbContext.Database.ExecuteSqlAsync(
                $"""
                DELETE FROM api_access.api_key_requests
                WHERE id IN (SELECT id FROM api_access.api_key_requests WHERE occurred_at < {cutoff} ORDER BY id LIMIT {BatchSize})
                """, cancellationToken);
            total += deleted;
        }
        while (deleted == BatchSize);

        LogPurged(logger, total, ApiKeyRequest.RetentionDays);
    }
}
