using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Support.Application;

namespace Modules.Support.Infrastructure;

/// <summary>
/// Soporte fase 2 (plan E2): corre <see cref="SupportThreadFollower"/> cada <see cref="SupportWorkerOptions.FollowIntervalSeconds"/>, una
/// pasada por tick en su propio scope. Varias réplicas pueden correrlo a la vez: el seguimiento es idempotente por índices (plan E3). Un
/// tick que falla se registra y el siguiente lo intenta de nuevo.
/// </summary>
internal sealed partial class SupportThreadFollowWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<SupportWorkerOptions> options,
    ILogger<SupportThreadFollowWorker> logger) : BackgroundService
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Support thread follow tick failed; it runs again on the next interval.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, settings.FollowIntervalSeconds)));
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<SupportThreadFollower>()
                    .ProcessBatchAsync(Math.Max(1, settings.BatchSize), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogTickFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
