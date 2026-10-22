using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Automations.Application;
using Modules.Automations.Application.Engine;
using Modules.Automations.Infrastructure.Options;

namespace Modules.Automations.Infrastructure.Engine;

/// <summary>
/// Spec 2026-10-21 §5.2: vacía <c>messaging.automation_inbox</c>. Reclama un lote en un scope y procesa cada fila en el suyo con
/// <see cref="RunProcessor"/>, como <c>MediaCopyWorker</c> (Mensajería). Varias réplicas pueden correrlo a la vez: el reclamo da un solo
/// ganador por fila y sólo la más vieja pendiente de cada conversación, y el lock por conversación de la unidad serializa el resto.
/// </summary>
internal sealed partial class AutomationInboxWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<AutomationsWorkerOptions> options,
    ILogger<AutomationInboxWorker> logger) : BackgroundService
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Automation inbox tick failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    // Sin texto del mensaje ni datos del cliente: sólo ids. La fila queda para su reintento por lease.
    [LoggerMessage(Level = LogLevel.Error, Message = "Automation unit for inbox row {MessageId} of tenant {TenantId} threw on attempt {Attempts}; it is retried when its lease expires.")]
    private static partial void LogUnitFailed(ILogger logger, Exception exception, Guid tenantId, Guid messageId, int attempts);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Value.InboxPollSeconds)));
        do
        {
            try
            {
                await DrainAsync(stoppingToken);
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

    /// <summary>Una pasada: internal para las pruebas.</summary>
    internal async Task DrainAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<InboundQueueItem> claimed;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var now = scope.ServiceProvider.GetRequiredService<IClock>().UtcNow;
            claimed = await scope.ServiceProvider.GetRequiredService<IAutomationInboundQueue>()
                .ClaimAsync(now, Math.Max(1, options.Value.InboxBatchSize), cancellationToken);
        }

        foreach (var item in claimed)
        {
            // Un scope por fila: el tracker de Automations y la transacción de la unidad no se comparten entre conversaciones.
            await using var scope = scopeFactory.CreateAsyncScope();
            try
            {
                await scope.ServiceProvider.GetRequiredService<RunProcessor>().ProcessAsync(item, cancellationToken);
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
}
