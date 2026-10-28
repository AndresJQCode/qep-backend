using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Modules.Integrations.Domain;
using Modules.Integrations.Infrastructure.Persistence;

namespace Modules.Integrations.Infrastructure.Shopify;

/// <summary>
/// Spec 2026-10-15 §9: borra las filas de <c>integrations.shopify_installs</c> vencidas (15 minutos sin canje, 7 días sin
/// enlace), al arrancar y cada <see cref="Interval"/>. Un solo <c>DELETE</c> por corrida, sin cargar filas.
/// <para>
/// <b>La excepción es la fila reclamada y sin token</b> (<c>state = 'claimed.…'</c>: el canje reclama la fila antes de llamar a Shopify): un canje que ganó el
/// reclamo justo antes del minuto 15 sigue hablando con Shopify después de <c>expires_at</c>, y su
/// <c>TryCompleteExchangeAsync</c> necesita la fila. Borrarla en ese momento le dejaría a la persona un
/// <c>install_not_found</c> con el <c>code</c> ya gastado. A esa fila se le da <see cref="ClaimedGrace"/> de más: después
/// del reclamo, el canje hace dos llamadas a Shopify (canje y tienda) de a lo sumo
/// <see cref="ShopifyAdminGateway.Timeout"/> cada una, así que pasados diez minutos ya no hay nadie que la vaya a guardar
/// y sólo queda la basura de un canje que falló.
/// </para>
/// Cualquier falla se registra y espera a la próxima corrida: no tumba el host. No audita: no hay persona y una
/// instalación no es de ningún tenant.
/// </summary>
internal sealed partial class ShopifyInstallSweeper(IServiceScopeFactory scopeFactory, ILogger<ShopifyInstallSweeper> logger)
    : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>Treinta veces lo que pueden tardar las dos llamadas del canje.</summary>
    internal static readonly TimeSpan ClaimedGrace = TimeSpan.FromMinutes(10);

    private readonly TaskCompletionSource _firstRun = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Se completa tras la primera corrida: las pruebas la esperan antes de sembrar.</summary>
    public Task FirstRunCompletion => _firstRun.Task;

    [LoggerMessage(Level = LogLevel.Information, Message = "Shopify install sweep removed {Removed} expired installs")]
    private static partial void LogSwept(ILogger logger, int removed);

    [LoggerMessage(Level = LogLevel.Error, Message = "Shopify install sweep failed; it runs again on the next interval.")]
    private static partial void LogFailed(ILogger logger, Exception exception);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Como el worker de re-cifrado: una base lenta no frena el arranque del host.
            await Task.Yield();
            try
            {
                await RunSafelyAsync(stoppingToken);
            }
            finally
            {
                _firstRun.TrySetResult();
            }

            using var timer = new PeriodicTimer(Interval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunSafelyAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // El host se apaga: no es una falla.
        }
    }

    /// <returns>Cuántas filas borró.</returns>
    internal async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<IntegrationsDbContext>();
        var now = scope.ServiceProvider.GetRequiredService<IClock>().UtcNow;
        var claimedCutoff = now - ClaimedGrace;
        // El punto del prefijo no es comodín de LIKE; sólo el % del final.
        var claimedPattern = ShopifyInstall.ClaimedStatePrefix + "%";
        var removed = await dbContext.ShopifyInstalls
            .Where(install => install.ExpiresAt < now
                && (install.ProtectedToken != null
                    || !EF.Functions.Like(install.State, claimedPattern)
                    || install.ExpiresAt < claimedCutoff))
            .ExecuteDeleteAsync(cancellationToken);
        LogSwept(logger, removed);
        return removed;
    }

    private async Task RunSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RunOnceAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogFailed(logger, exception);
        }
    }
}
