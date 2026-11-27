using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Modules.Notifications.Infrastructure.HrBirthdays;

/// <summary>
/// HR birthday notices, spec 2026-11-26 §5.3: once a minute it runs <see cref="HrBirthdayDeliveryProcessor"/> passes, each in a new scope. A pass
/// that claims a full batch is followed by another one in the same tick (bounded by <see cref="MaxPassesPerTick"/>). Every replica runs it: the
/// processor claims rows with <c>FOR UPDATE SKIP LOCKED</c> and a lease, so they never take the same one. A pass that throws is logged and the
/// next tick tries again.
/// </summary>
internal sealed partial class HrBirthdayDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<HrBirthdayDeliveryWorker> logger) : BackgroundService
{
    internal static readonly TimeSpan Period = TimeSpan.FromMinutes(1);

    /// <summary>Upper bound of passes per tick: it never hogs the host if the sends are slow.</summary>
    internal const int MaxPassesPerTick = 20;

    [LoggerMessage(Level = LogLevel.Error, Message = "A birthday delivery pass failed; it runs again on the next tick.")]
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
            // A short pass means nothing more was claimable; a full one may have left rows behind.
            for (var pass = 0; pass < MaxPassesPerTick; pass++)
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var now = scope.ServiceProvider.GetRequiredService<IClock>().UtcNow;
                var claimed = await scope.ServiceProvider.GetRequiredService<HrBirthdayDeliveryProcessor>()
                    .RunAsync(now, cancellationToken);
                if (claimed < HrBirthdayDeliveryProcessor.BatchSize)
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
