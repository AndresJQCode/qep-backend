using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Modules.Integrations.Application;
using Modules.Integrations.Application.Shopify;
using Modules.Integrations.Domain;
using Modules.Integrations.Infrastructure.Persistence;

namespace Modules.Integrations.Infrastructure.Shopify;

internal sealed record ShopifyTokenRefreshRunResult(int Refreshed, int Rejected, int Deferred, int Skipped);

/// <summary>
/// Spec 2026-11-12 §6.2 y §6.3: el único que refresca el token offline de una conexión de Shopify. Al arrancar y cada
/// <see cref="ShopifyTokenRefresh.Interval"/>, con el molde de <c>InstagramTokenRefreshWorker</c>: un scope por conexión y
/// nunca tumba el host.
/// <list type="bullet">
/// <item>Entran todos los estados salvo <c>NeedsAttention</c> con <c>token_expired</c> o <c>app_uninstalled</c>: una pausada
/// tiene que poder reanudarse sin reautorizar, y una en atención por otra causa se perdería si su token venciera.</item>
/// <item>Un solo escritor por tienda (anti-objetivo 1): el refresh token rota, así que el refresco va bajo
/// <c>pg_try_advisory_xact_lock</c> por conexión. El <c>Version</c> optimista no alcanza: el perdedor ya habría gastado
/// el refresh token. Ocupado, se salta y vuelve en la próxima vuelta.</item>
/// <item>Bajo el candado se relee la conexión: si el reenlace cambió el refresh token (la fecha de su secreto) o ya no hace
/// falta refrescar, no se llama a Shopify.</item>
/// <item>Un 4xx (salvo 429) es definitivo: <see cref="IConnectionHealthReporter.ReportTokenExpiredAsync"/>, desde cualquier
/// estado. Un 429, 5xx, red o respuesta ilegible esperan a la próxima vuelta (D13).</item>
/// <item>El refresco no cambia el estado ni la verificación. Si el guardado falla después de que Shopify rotó, el par viejo
/// sigue sirviendo hasta que alguien use el nuevo, que nadie tiene: la próxima vuelta refresca con el viejo.</item>
/// <item>Nada del token ni de la respuesta sale a los logs; tampoco se audita (Instagram no lo hace).</item>
/// </list>
/// <see cref="FirstRunCompletion"/> se completa siempre tras la primera corrida: las pruebas la esperan.
/// </summary>
internal sealed partial class ShopifyTokenRefreshWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<ShopifyTokenRefreshWorker> logger) : BackgroundService
{
    private const string Unchanged = "unchanged";

    private readonly TaskCompletionSource _firstRun = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task FirstRunCompletion => _firstRun.Task;

    private enum Outcome
    {
        Refreshed,
        Rejected,
        Deferred,
        Skipped,
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Shopify connection {ConnectionId}: token refresh {Outcome} ({Reason}); the token expires at {ExpiresAt}")]
    private static partial void LogOutcome(ILogger logger, Guid connectionId, string outcome, string reason, string expiresAt);

    [LoggerMessage(Level = LogLevel.Information, Message = "shopify token refresh finished: {Refreshed} refreshed, {Rejected} rejected, {Deferred} deferred, {Skipped} skipped")]
    private static partial void LogFinished(ILogger logger, int refreshed, int rejected, int deferred, int skipped);

    [LoggerMessage(Level = LogLevel.Error, Message = "Shopify token refresh failed; it runs again on the next interval.")]
    private static partial void LogFailed(ILogger logger, Exception exception);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Yield();
            try
            {
                await RunSafelyAsync(stoppingToken);
            }
            finally
            {
                _firstRun.TrySetResult();
            }

            using var timer = new PeriodicTimer(ShopifyTokenRefresh.Interval);
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

    internal async Task<ShopifyTokenRefreshRunResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        List<(Guid Id, DateTimeOffset? RefreshTokenSavedAt)> due;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var now = scope.ServiceProvider.GetRequiredService<IClock>().UtcNow;
            var providerKey = IntegrationProviders.Shopify.Key;
            var connections = await scope.ServiceProvider.GetRequiredService<IntegrationsDbContext>().Connections
                .AsNoTracking()
                .Where(connection => connection.ProviderKey == providerKey)
                .ToListAsync(cancellationToken);
            due = connections
                .Where(connection => !ShopifyTokenRefresh.IsUnrecoverable(connection.Status, connection.LastFailureCode))
                .Select(connection => (connection.Id, RefreshTokenSavedAt: RefreshSecretOf(connection)?.UpdatedAt, connection.Fields))
                .Where(candidate => candidate.RefreshTokenSavedAt is null
                    || ShopifyTokenRefresh.IsDue(candidate.Fields.GetValueOrDefault(ShopifyFieldKeys.TokenExpiresAt), now))
                .Select(candidate => (candidate.Id, candidate.RefreshTokenSavedAt))
                .ToList();
        }

        int refreshed = 0, rejected = 0, deferred = 0, skipped = 0;
        foreach (var (connectionId, savedAt) in due)
        {
            switch (await RefreshOneAsync(connectionId, savedAt, cancellationToken))
            {
                case Outcome.Refreshed:
                    refreshed++;
                    break;
                case Outcome.Rejected:
                    rejected++;
                    break;
                case Outcome.Deferred:
                    deferred++;
                    break;
                default:
                    skipped++;
                    break;
            }
        }

        LogFinished(logger, refreshed, rejected, deferred, skipped);
        return new ShopifyTokenRefreshRunResult(refreshed, rejected, deferred, skipped);
    }

    private static ConnectionSecret? RefreshSecretOf(IntegrationConnection connection) =>
        connection.Secrets.FirstOrDefault(secret => string.Equals(secret.FieldKey, ShopifyFieldKeys.RefreshToken, StringComparison.Ordinal));

    private async Task<Outcome> RefreshOneAsync(Guid connectionId, DateTimeOffset? listedSavedAt, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var unitOfWork = services.GetRequiredService<IIntegrationsUnitOfWork>();
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        if (!await unitOfWork.TryLockShopifyTokenAsync(connectionId, cancellationToken))
        {
            // Otra réplica o una vuelta solapada lo está refrescando, o un reenlace está reemplazando los secretos.
            return Outcome.Skipped;
        }

        var now = services.GetRequiredService<IClock>().UtcNow;
        var connection = await services.GetRequiredService<IntegrationsDbContext>().Connections
            .SingleOrDefaultAsync(stored => stored.Id == connectionId, cancellationToken);
        if (connection is null || ShopifyTokenRefresh.IsUnrecoverable(connection.Status, connection.LastFailureCode))
        {
            return Outcome.Skipped;
        }

        var secret = RefreshSecretOf(connection);
        if (secret?.UpdatedAt != listedSavedAt
            || (secret is not null && !ShopifyTokenRefresh.IsDue(connection.Fields.GetValueOrDefault(ShopifyFieldKeys.TokenExpiresAt), now)))
        {
            // Lo reemplazó el reenlace o lo refrescó otra vuelta entre la lista y el candado.
            return Outcome.Skipped;
        }

        if (secret is null)
        {
            // §6.3: una conexión anterior a los tokens que expiran no tiene con qué refrescar; la persona reconecta.
            await services.GetRequiredService<IConnectionHealthReporter>()
                .ReportTokenExpiredAsync(connection.TenantId, connection.Id, cancellationToken);
            LogOutcome(logger, connection.Id, "rejected", "no_refresh_token", Unchanged);
            return Outcome.Rejected;
        }

        var protector = services.GetRequiredService<ISecretProtector>();
        if (!protector.TryUnprotect(connection.Id, secret.FieldKey, secret.Protected, out var refreshToken))
        {
            // Llave retirada o bytes dañados: la pantalla ya lo muestra como no legible; reconectar lo arregla.
            LogOutcome(logger, connection.Id, "transient", "unreadable_secret", Unchanged);
            return Outcome.Deferred;
        }

        ShopifyTokenGrant grant;
        try
        {
            grant = await services.GetRequiredService<IShopifyAdminGateway>().RefreshTokenAsync(
                connection.Fields.GetValueOrDefault(ShopifyFieldKeys.ShopDomain) ?? string.Empty, refreshToken, cancellationToken);
        }
        catch (ShopifyGatewayException exception) when (exception.Kind == ShopifyGatewayFailure.Rejected)
        {
            // El guardado del reporte confirma la transacción, con el candado todavía tomado.
            await services.GetRequiredService<IConnectionHealthReporter>()
                .ReportTokenExpiredAsync(connection.TenantId, connection.Id, cancellationToken);
            LogOutcome(logger, connection.Id, "rejected", "refresh_rejected", Unchanged);
            return Outcome.Rejected;
        }
        catch (ShopifyGatewayException exception)
        {
            // D13: un Invalid no prueba que el refresh token murió; marcarlo cortaría una tienda sana.
            LogOutcome(logger, connection.Id, "transient", exception.Kind == ShopifyGatewayFailure.Invalid ? "invalid_response" : "unreachable", Unchanged);
            return Outcome.Deferred;
        }

        var expiresAt = now + grant.ExpiresIn;
        var fields = new Dictionary<string, string>(connection.Fields, StringComparer.Ordinal)
        {
            [ShopifyFieldKeys.TokenExpiresAt] = ShopifyTokenRefresh.Format(expiresAt),
            [ShopifyFieldKeys.RefreshTokenExpiresAt] = ShopifyTokenRefresh.Format(now + grant.RefreshTokenExpiresIn),
        };
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ShopifyFieldKeys.AccessToken] = grant.AccessToken,
            [ShopifyFieldKeys.RefreshToken] = grant.RefreshToken,
        };
        try
        {
            connection.Update(IntegrationProviders.Shopify, connection.Name, fields, secrets, protector.Protect, now, verified: false);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // §6.2: Shopify ya rotó, pero el par viejo sigue sirviendo y la próxima vuelta lo usa. La excepción no se
            // registra: un error de validación del dominio podría citar un valor.
            LogOutcome(logger, connection.Id, "transient", "save_failed", Unchanged);
            return Outcome.Deferred;
        }

        var newExpiry = ShopifyTokenRefresh.Format(expiresAt);
        LogOutcome(logger, connection.Id, "ok", "refreshed", newExpiry);
        return Outcome.Refreshed;
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
