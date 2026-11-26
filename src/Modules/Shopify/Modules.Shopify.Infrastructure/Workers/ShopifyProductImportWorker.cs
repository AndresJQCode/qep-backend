using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Modules.Shopify.Application;
using Modules.Shopify.Domain;

namespace Modules.Shopify.Infrastructure.Workers;

/// <summary>
/// Spec 2026-11-17 §5.2: reclama las importaciones de productos una por una (<see cref="IShopifyProductImportQueue"/>, <c>FOR
/// UPDATE SKIP LOCKED</c> con lease de 2 min) y corre cada una en su propio scope. Espera el timbre de
/// <see cref="DoorbellQueues.ShopifyProducts"/> (el <c>INSERT</c> del <c>POST</c> lo toca), como <c>ExportJobWorker</c>. Una excepción
/// inesperada sigue la escalera de las fallas de Shopify: vuelve a la cola con <see cref="ShopifyProductImportProcessor.RetryDelay"/>
/// y en el intento <see cref="ShopifyProductImportProcessor.MaxAttempts"/> termina <c>unreachable</c>.
/// </summary>
internal sealed partial class ShopifyProductImportWorker(
    IQueueDoorbell doorbell, IServiceScopeFactory scopeFactory, ILogger<ShopifyProductImportWorker> logger) : BackgroundService
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    /// <summary>Tope de importaciones por pasada: el resto espera al tick siguiente.</summary>
    internal const int MaxImportsPerTick = 20;

    [LoggerMessage(Level = LogLevel.Error, Message = "Shopify product import tick failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shopify product import {ImportId} failed on attempt {Attempt} of {MaxAttempts}; it is retried later.")]
    private static partial void LogAttemptFailed(ILogger logger, Exception exception, Guid importId, int attempt, int maxAttempts);

    [LoggerMessage(Level = LogLevel.Error, Message = "Shopify product import {ImportId} gave up on its last attempt ({Attempt}): unreachable.")]
    private static partial void LogGaveUp(ILogger logger, Exception exception, Guid importId, int attempt);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var listener = doorbell.Listen(DoorbellQueues.ShopifyProducts);
        await DoorbellPolling.RunAsync(
            listener,
            PollInterval,
            DoorbellQueues.SafetyNet(DoorbellQueues.ShopifyProducts),
            async _ =>
            {
                try
                {
                    await DrainAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    LogTickFailed(logger, exception);
                }

                return false;
            },
            stoppingToken);
    }

    /// <summary>Una pasada (internal para las pruebas): reclama y procesa hasta que no quede ninguna vencida.</summary>
    internal async Task DrainAsync(CancellationToken cancellationToken)
    {
        for (var processed = 0; processed < MaxImportsPerTick; processed++)
        {
            ShopifyProductImportClaim? claim;
            await using (var claimScope = scopeFactory.CreateAsyncScope())
            {
                var now = claimScope.ServiceProvider.GetRequiredService<IClock>().UtcNow;
                claim = await claimScope.ServiceProvider.GetRequiredService<IShopifyProductImportQueue>().TryClaimNextAsync(now, cancellationToken);
            }

            if (claim is null)
            {
                return;
            }

            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ShopifyProductImportProcessor>().ProcessAsync(claim, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // Scope nuevo: el del procesador quedó con lo que haya dejado a medias.
                await using var recovery = scopeFactory.CreateAsyncScope();
                var queue = recovery.ServiceProvider.GetRequiredService<IShopifyProductImportQueue>();
                var now = recovery.ServiceProvider.GetRequiredService<IClock>().UtcNow;
                if (claim.Attempts >= ShopifyProductImportProcessor.MaxAttempts)
                {
                    LogGaveUp(logger, exception, claim.ImportId, claim.Attempts);
                    await queue.MarkFailedAsync(claim.ImportId, ShopifyProductCodes.Unreachable, now, cancellationToken);
                }
                else
                {
                    LogAttemptFailed(logger, exception, claim.ImportId, claim.Attempts, ShopifyProductImportProcessor.MaxAttempts);
                    await queue.RenewAsync(claim.ImportId, now + ShopifyProductImportProcessor.RetryDelay(claim.Attempts, null), cancellationToken);
                }
            }
        }
    }
}
