using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Shopify.Application;

namespace Modules.Shopify.Infrastructure.Workers;

/// <summary>
/// Spec 2026-10-15 §5.3: la conciliación de todas las tiendas operativas cada <c>Shopify:Reconcile:IntervalMinutes</c>.
/// <para><b>Un scope por corrida para la lista y uno por tienda.</b> <c>ListOperationalAsync</c> trae las conexiones de todos los
/// tenants (con sus tokens) leídas con tracking: ese scope se cierra antes de tocar una tienda. Cada tienda corre en el suyo, con
/// su <c>ShopifyDbContext</c> y su unidad de trabajo, porque el importador hace un <c>SaveChanges</c> a mitad de camino que
/// confirmaría lo que otra tienda hubiera dejado rastreado. El lease va por <c>UPDATE</c> directo (<see cref="IShopifyReconcileLeases"/>),
/// así que tampoco queda pendiente en el tracker.</para>
/// <para>Una tienda que falla no frena a las demás: queda en el log con su tenant y su conexión (nunca el token ni el mensaje de la
/// excepción, que podría traer datos del pedido) y la próxima corrida vuelve desde la misma ventana. Una con lease vigente (otra
/// réplica o «Sincronizar ahora») se salta.</para>
/// </summary>
internal sealed partial class ShopifyReconcileWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<ShopifyReconcileOptions> options,
    ILogger<ShopifyReconcileWorker> logger) : BackgroundService
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Shopify reconciliation tick failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Shopify store {ConnectionId} of tenant {TenantId} reconciled: {Fetched} fetched, {Imported} imported, {Waiting} waiting, {Cancelled} cancelled, {Ignored} ignored, {Failed} failed ({FirstError}) in {DurationMs} ms.")]
    private static partial void LogReconciled(
        ILogger logger, Guid connectionId, Guid tenantId, int fetched, int imported, int waiting, int cancelled, int ignored, int failed, string firstError, long durationMs);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Shopify store {ConnectionId} of tenant {TenantId} skipped: another reconciliation holds its lease.")]
    private static partial void LogSkipped(ILogger logger, Guid connectionId, Guid tenantId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shopify store {ConnectionId} of tenant {TenantId} was not reconciled: {Code}.")]
    private static partial void LogStoreFailed(ILogger logger, Guid connectionId, Guid tenantId, string code);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, options.Value.IntervalMinutes)));
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
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

    /// <summary>Una pasada sobre todas las tiendas operativas (internal para las pruebas).</summary>
    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ShopifyConnectionRef> connections;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            connections = await scope.ServiceProvider.GetRequiredService<IShopifyConnectionDirectory>().ListOperationalAsync(cancellationToken);
        }

        foreach (var connection in connections)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            try
            {
                var result = await scope.ServiceProvider.GetRequiredService<ReconcileStore>().RunAsync(connection, cancellationToken);
                LogReconciled(
                    logger,
                    connection.ConnectionId,
                    connection.TenantId,
                    result.Fetched,
                    result.Imported,
                    result.Waiting,
                    result.Cancelled,
                    result.Ignored,
                    result.Failed,
                    result.FirstError ?? "none",
                    result.DurationMs);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ResourceConflictException)
            {
                LogSkipped(logger, connection.ConnectionId, connection.TenantId);
            }
            catch (Exception exception)
            {
                LogStoreFailed(logger, connection.ConnectionId, connection.TenantId, Code(exception));
            }
        }
    }

    private static string Code(Exception exception) => exception switch
    {
        ServiceUnavailableException unavailable => unavailable.Code,
        BuildingBlocks.Domain.DomainException domain => domain.Code,
        RequestConcurrencyException concurrency => concurrency.Code,
        _ => exception.GetType().Name,
    };
}

/// <summary>Spec 2026-10-15 §5.3 y §10: cada cuántos minutos corre la conciliación (D-S1: 10).</summary>
public sealed class ShopifyReconcileOptions
{
    public const string SectionName = "Shopify:Reconcile";

    public int IntervalMinutes { get; set; } = 10;
}

/// <summary>Un cero o un negativo dejaría la conciliación corriendo sin pausa (o sin correr); se valida al arrancar.</summary>
internal sealed class ShopifyReconcileOptionsValidator : IValidateOptions<ShopifyReconcileOptions>
{
    public ValidateOptionsResult Validate(string? name, ShopifyReconcileOptions options) =>
        options.IntervalMinutes > 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail($"{ShopifyReconcileOptions.SectionName}:IntervalMinutes must be greater than zero.");
}
