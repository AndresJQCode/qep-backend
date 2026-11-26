using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Modules.Notifications.Infrastructure.TaskNotices;

/// <summary>
/// Task-notices spec 2026-11-22 §5.4: once a minute it runs <see cref="TaskNoticeEscalationProcessor"/> passes, each in a new scope. A pass
/// that claims a full batch is followed by another one in the same tick (bounded by <see cref="MaxPassesPerTick"/>), so rows deferred every
/// minute for connected members cannot starve the ones that are really due. Every
/// replica runs it: the processor claims rows with <c>FOR UPDATE SKIP LOCKED</c> and a lease, so they never take the same one. A pass that
/// throws is logged and the next tick tries again; the minute is also the granularity of the presence deferral.
/// </summary>
internal sealed partial class TaskNoticeEscalationWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<TaskNoticeEscalationWorker> logger) : BackgroundService
{
    internal static readonly TimeSpan Period = TimeSpan.FromMinutes(1);

    /// <summary>Upper bound of passes per tick: 20 passes of <see cref="TaskNoticeEscalationProcessor.BatchSize"/> rows, well inside the minute
    /// for rows that are mostly deferred, and it never hogs the host if the sends are slow.</summary>
    internal const int MaxPassesPerTick = 20;

    [LoggerMessage(Level = LogLevel.Error, Message = "A task notice escalation pass failed; it runs again on the next tick.")]
    private static partial void LogFailed(ILogger logger, Exception exception);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Period);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await TickAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown.
        }
    }

    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        try
        {
            // A short pass means nothing more was due; a full one may have left rows behind. A deferred row moves its due time a minute
            // ahead, so it cannot be claimed again in the same tick and the loop always ends.
            for (var pass = 0; pass < MaxPassesPerTick; pass++)
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var now = scope.ServiceProvider.GetRequiredService<IClock>().UtcNow;
                var claimed = await scope.ServiceProvider.GetRequiredService<TaskNoticeEscalationProcessor>()
                    .RunAsync(now, cancellationToken);
                if (claimed < TaskNoticeEscalationProcessor.BatchSize)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogFailed(logger, exception);
        }
    }
}
