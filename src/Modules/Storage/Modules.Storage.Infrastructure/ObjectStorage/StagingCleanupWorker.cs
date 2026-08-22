using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Storage.Application;
using Modules.Storage.Domain;
using Modules.Storage.Infrastructure.Persistence;

namespace Modules.Storage.Infrastructure.ObjectStorage;

internal sealed partial class StagingCleanupWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<StorageOptions> options,
    ILogger<StagingCleanupWorker> logger) : BackgroundService
{
    private const int BatchSize = 100;

    [LoggerMessage(Level = LogLevel.Error, Message = "Storage staging cleanup tick failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(
            TimeSpan.FromMinutes(options.Value.StagingCleanupMinutes));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ProcessBatchAsync(stoppingToken);
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
    }

    private async Task ProcessBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<StorageDbContext>();
        var objectStorage = scope.ServiceProvider.GetRequiredService<IObjectStorage>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();
        var cutoff = clock.UtcNow.AddHours(-options.Value.StagingRetentionHours);
        var abandoned = await dbContext.FileResources
            .Where(file => file.Status == FileResourceStatus.PendingUpload)
            .Where(file => file.CreatedAt < cutoff)
            .OrderBy(file => file.CreatedAt)
            .Take(BatchSize)
            .ToArrayAsync(cancellationToken);

        foreach (var file in abandoned)
        {
            await objectStorage.DeleteAsync(file.StorageKey, cancellationToken);
            file.PurgeAbandonedUpload(clock.UtcNow);
        }
        if (abandoned.Length > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }
}
