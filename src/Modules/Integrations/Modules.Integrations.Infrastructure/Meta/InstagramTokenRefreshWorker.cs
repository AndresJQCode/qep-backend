using System.Globalization;
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Integrations.Infrastructure.Persistence;

namespace Modules.Integrations.Infrastructure.Meta;

/// <summary>La sección <c>Integrations:Instagram</c> (spec 2026-10-14 §9): cada cuánto corre el refresco y con cuántos
/// días de margen se refresca un token.</summary>
public sealed class InstagramRefreshOptions
{
    public const string SectionName = "Integrations:Instagram";

    /// <summary>Diario. Un token vive 60 días y se refresca con 15 de margen: un pod caído una semana no lo pierde.</summary>
    public int RefreshIntervalMinutes { get; set; } = 1440;

    public int RefreshBeforeExpiryDays { get; set; } = 15;
}

internal sealed record InstagramRefreshRunResult(int Refreshed, int Rejected, int Deferred);

/// <summary>
/// Spec 2026-10-14, decisión 5: refresca el token de cada conexión <c>instagram</c> <c>Active</c> a la que le quedan
/// menos de <see cref="InstagramRefreshOptions.RefreshBeforeExpiryDays"/> días, con el molde de
/// <c>ConnectionSecretRekeyWorker</c>: al arrancar y cada intervalo, un scope por conexión, nunca tumba el host.
/// <list type="bullet">
/// <item>Meta sólo refresca un token con más de 24 h: uno más nuevo (la fecha del secreto) se deja para la próxima.</item>
/// <item>Un 4xx de Meta es definitivo: la conexión pasa a <c>NeedsAttention</c> con <c>token_expired</c> por
/// <see cref="IConnectionHealthReporter"/>, que audita y publica. La persona vuelve a conectar.</item>
/// <item>Timeout, red o 5xx se registran y esperan a la próxima corrida.</item>
/// <item>El token nuevo entra por <see cref="IntegrationConnection.Update"/>, que lo cifra y marca la verificación; un
/// <c>PUT</c> en el medio choca por versión y la conexión vuelve en la próxima corrida.</item>
/// </list>
/// <see cref="FirstRunCompletion"/> se completa siempre tras la primera corrida: las pruebas la esperan.
/// </summary>
internal sealed partial class InstagramTokenRefreshWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<InstagramRefreshOptions> options,
    ILogger<InstagramTokenRefreshWorker> logger) : BackgroundService
{
    /// <summary>Meta: «the token must be at least 24 hours old».</summary>
    public static readonly TimeSpan MinimumTokenAge = TimeSpan.FromHours(24);

    private readonly TaskCompletionSource _firstRun = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task FirstRunCompletion => _firstRun.Task;

    internal TimeSpan Interval { get; } = TimeSpan.FromMinutes(Math.Max(1, options.Value.RefreshIntervalMinutes));

    internal TimeSpan RefreshBefore { get; } = TimeSpan.FromDays(Math.Max(1, options.Value.RefreshBeforeExpiryDays));

    [LoggerMessage(Level = LogLevel.Information, Message = "instagram token refresh finished: {Refreshed} refreshed, {Rejected} rejected, {Deferred} deferred")]
    private static partial void LogFinished(ILogger logger, int refreshed, int rejected, int deferred);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Instagram connection {ConnectionId}: Meta rejected the token refresh (HTTP {HttpStatus}, code {Code}); the connection needs attention.")]
    private static partial void LogRejected(ILogger logger, Guid connectionId, int httpStatus, int? code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Instagram connection {ConnectionId}: the token refresh could not reach Meta ({Reason}); it is retried on the next run.")]
    private static partial void LogDeferred(ILogger logger, Guid connectionId, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Instagram token refresh failed; it runs again on the next interval.")]
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

    internal async Task<InstagramRefreshRunResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        List<Guid> due;
        DateTimeOffset now;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            now = scope.ServiceProvider.GetRequiredService<IClock>().UtcNow;
            var dbContext = scope.ServiceProvider.GetRequiredService<IntegrationsDbContext>();
            var providerKey = IntegrationProviders.Instagram.Key;
            var candidates = await dbContext.Connections
                .AsNoTracking()
                .Where(connection => connection.ProviderKey == providerKey && connection.Status == ConnectionStatus.Active)
                .Select(connection => new { connection.Id, connection.Fields })
                .ToListAsync(cancellationToken);
            due = candidates
                .Where(candidate => IsDue(candidate.Fields.GetValueOrDefault(InstagramFieldKeys.TokenExpiresAt), now, RefreshBefore))
                .Select(candidate => candidate.Id)
                .ToList();
        }

        var refreshed = 0;
        var rejected = 0;
        var deferred = 0;
        foreach (var connectionId in due)
        {
            switch (await RefreshOneAsync(connectionId, now, cancellationToken))
            {
                case Outcome.Refreshed:
                    refreshed++;
                    break;
                case Outcome.Rejected:
                    rejected++;
                    break;
                default:
                    deferred++;
                    break;
            }
        }

        LogFinished(logger, refreshed, rejected, deferred);
        return new InstagramRefreshRunResult(refreshed, rejected, deferred);
    }

    /// <summary>Sin fecha legible se refresca: es mejor un refresco de más que un token vencido sin aviso.</summary>
    internal static bool IsDue(string? tokenExpiresAt, DateTimeOffset now, TimeSpan refreshBefore) =>
        !DateTimeOffset.TryParse(tokenExpiresAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expiresAt)
        || expiresAt - now <= refreshBefore;

    private enum Outcome
    {
        Refreshed,
        Rejected,
        Deferred,
    }

    private async Task<Outcome> RefreshOneAsync(Guid connectionId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<IntegrationsDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
        var gateway = scope.ServiceProvider.GetRequiredService<IInstagramLoginGateway>();
        var connection = await dbContext.Connections.SingleOrDefaultAsync(connection => connection.Id == connectionId, cancellationToken);
        if (connection is not { Status: ConnectionStatus.Active })
        {
            return Outcome.Deferred;
        }

        var secret = connection.Secrets.FirstOrDefault(stored => string.Equals(stored.FieldKey, InstagramFieldKeys.AccessToken, StringComparison.Ordinal));
        if (secret is null || now - secret.UpdatedAt < MinimumTokenAge)
        {
            return Outcome.Deferred;
        }

        if (!protector.TryUnprotect(connection.Id, secret.FieldKey, secret.Protected, out var current))
        {
            // Llave retirada o bytes dañados: la pantalla ya lo muestra como no legible; reconectar lo arregla.
            LogDeferred(logger, connection.Id, "unreadable_secret");
            return Outcome.Deferred;
        }

        var refreshedToken = await gateway.RefreshAsync(current, cancellationToken);
        if (!refreshedToken.Succeeded || refreshedToken.Value is not { } token || string.IsNullOrWhiteSpace(token.AccessToken))
        {
            var failure = refreshedToken.Failure ?? new GraphFailure(0, null, null, "no_token");
            if (failure.HttpStatus is 0 or 408 or >= 500)
            {
                LogDeferred(logger, connection.Id, failure.Reason);
                return Outcome.Deferred;
            }

            LogRejected(logger, connection.Id, failure.HttpStatus, failure.Code);
            await scope.ServiceProvider.GetRequiredService<IConnectionHealthReporter>()
                .ReportCredentialsRejectedAsync(connection.TenantId, connection.Id, ConnectionFailureCodes.TokenExpired, cancellationToken);
            return Outcome.Rejected;
        }

        var provider = IntegrationProviders.Instagram;
        var fields = new Dictionary<string, string>(connection.Fields, StringComparer.Ordinal)
        {
            [InstagramFieldKeys.TokenExpiresAt] = ConnectInstagramHandler.TokenExpiry(now, token.ExpiresIn),
        };
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal) { [InstagramFieldKeys.AccessToken] = token.AccessToken };
        connection.Update(provider, connection.Name, fields, secrets, protector.Protect, now, verified: true);
        try
        {
            await scope.ServiceProvider.GetRequiredService<IIntegrationsUnitOfWork>().SaveChangesAsync(cancellationToken);
            return Outcome.Refreshed;
        }
        catch (RequestConcurrencyException)
        {
            // Un PUT guardó esta conexión en el medio: vuelve en la próxima corrida.
            return Outcome.Deferred;
        }
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
