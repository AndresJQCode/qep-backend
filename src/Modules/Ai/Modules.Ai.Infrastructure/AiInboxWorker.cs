using BuildingBlocks.Application;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Ai.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Ai.Infrastructure;

/// <summary>§6.8: el módulo <c>ai</c> del tenant, con la caché por pod de 60 s de <c>AutomationsModuleStatus</c>; sin ella sería una lectura de
/// Tenancy por entrante.</summary>
internal sealed class AiModuleStatus(ITenantModules tenantModules, IMemoryCache cache) : IAiModuleStatus
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    /// <summary>Internal para las pruebas que apagan el módulo con el host ya en marcha.</summary>
    internal static string CacheKey(Guid tenantId) => $"ai:module:{tenantId}";

    public Task<bool> IsEnabledAsync(Guid tenantId, CancellationToken cancellationToken) =>
        cache.GetOrCreateAsync(CacheKey(tenantId), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = Ttl;
            var set = await tenantModules.FindAsync(tenantId, cancellationToken);
            // null = el stub de desarrollo: no bloquea, como TenantModuleGuard.
            return set is null || set.IsEnabled(TenantModuleKeys.Ai);
        });
}

/// <summary>
/// Spec 2026-10-29 §6.2 (etapa 1) y §6.8: vacía las filas <c>bot_kind = 2</c> de <c>messaging.automation_inbox</c>, cada una en su scope, y
/// corre los barridos de sesiones abandonadas (D-A19, cada 10 min) y de tenants con el módulo apagado (§6.8, cada minuto). Los tiempos
/// son de <see cref="AiWorkerOptions"/>.
/// </summary>
internal sealed partial class AiInboxWorker(
    IServiceScopeFactory scopeFactory, IOptions<AiWorkerOptions> options, ILogger<AiInboxWorker> logger) : BackgroundService
{
    /// <summary>§6.2: hasta 20 filas por pasada, cada una reclamada justo antes de procesarla para que empiece con su lease entero.</summary>
    public const int InboxBatchSize = 20;

    private DateTimeOffset _nextIdleSweep = DateTimeOffset.MinValue;
    private DateTimeOffset _nextModuleSweep = DateTimeOffset.MinValue;

    [LoggerMessage(Level = LogLevel.Error, Message = "AI inbox worker tick failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "AI inbox row {MessageId} of tenant {TenantId} threw on attempt {Attempts}; it is retried when its lease expires.")]
    private static partial void LogUnitFailed(ILogger logger, Exception exception, Guid tenantId, Guid messageId, int attempts);

    [LoggerMessage(Level = LogLevel.Information, Message = "AI session sweep closed {Count} sessions ({Kind}).")]
    private static partial void LogSwept(ILogger logger, int count, string kind);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Value.PollSeconds)));
        do
        {
            try
            {
                await DrainAsync(stoppingToken);
                await SweepAsync(stoppingToken);
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

    /// <summary>Una pasada de la cola. Internal para las pruebas.</summary>
    internal async Task DrainAsync(CancellationToken cancellationToken)
    {
        for (var processed = 0; processed < InboxBatchSize; processed++)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var now = scope.ServiceProvider.GetRequiredService<IClock>().UtcNow;
            var claimed = await scope.ServiceProvider.GetRequiredService<IAiInbox>().ClaimAsync(now, 1, cancellationToken);
            if (claimed.Count == 0)
            {
                return;
            }

            var item = claimed[0];
            try
            {
                await scope.ServiceProvider.GetRequiredService<AiInboxProcessor>().ProcessAsync(item, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogUnitFailed(logger, exception, item.TenantId, item.MessageId, item.Attempts);
            }
        }
    }

    /// <summary>Los barridos, cada uno con su periodo. Internal para las pruebas, que los fuerzan con <paramref name="force"/>.</summary>
    internal async Task SweepAsync(CancellationToken cancellationToken, bool force = false)
    {
        var now = DateTimeOffset.UtcNow;
        if (force || now >= _nextModuleSweep)
        {
            _nextModuleSweep = now + TimeSpan.FromSeconds(Math.Max(1, options.Value.ModuleSweepSeconds));
            await using var scope = scopeFactory.CreateAsyncScope();
            var closed = await scope.ServiceProvider.GetRequiredService<AiSessionSweeper>().SweepDisabledModuleAsync(cancellationToken);
            if (closed > 0)
            {
                LogSwept(logger, closed, "module_disabled");
            }
        }

        if (force || now >= _nextIdleSweep)
        {
            _nextIdleSweep = now + TimeSpan.FromMinutes(Math.Max(1, options.Value.IdleSweepMinutes));
            await using var scope = scopeFactory.CreateAsyncScope();
            var closed = await scope.ServiceProvider.GetRequiredService<AiSessionSweeper>().SweepIdleAsync(cancellationToken);
            if (closed > 0)
            {
                LogSwept(logger, closed, "abandoned");
            }
        }
    }
}
