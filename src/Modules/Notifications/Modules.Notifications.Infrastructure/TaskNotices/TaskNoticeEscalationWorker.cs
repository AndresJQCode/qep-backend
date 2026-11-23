using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Modules.Notifications.Infrastructure.TaskNotices;

/// <summary>
/// Task-notices spec 2026-11-22 §5.4: once a minute it runs one pass of <see cref="TaskNoticeEscalationProcessor"/> in a new scope. Every
/// replica runs it: the processor claims rows with <c>FOR UPDATE SKIP LOCKED</c> and a lease, so they never take the same one. A pass that
/// throws is logged and the next tick tries again; the minute is also the granularity of the presence deferral.
/// </summary>
internal sealed partial class TaskNoticeEscalationWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<TaskNoticeEscalationWorker> logger) : BackgroundService
{
    internal static readonly TimeSpan Period = TimeSpan.FromMinutes(1);

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
            await using var scope = scopeFactory.CreateAsyncScope();
            var now = scope.ServiceProvider.GetRequiredService<IClock>().UtcNow;
            await scope.ServiceProvider.GetRequiredService<TaskNoticeEscalationProcessor>().RunAsync(now, cancellationToken);
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
