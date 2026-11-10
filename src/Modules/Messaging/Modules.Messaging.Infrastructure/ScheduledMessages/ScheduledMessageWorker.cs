using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Messaging.Application;
using Modules.Messaging.Infrastructure.Options;

namespace Modules.Messaging.Infrastructure.ScheduledMessages;

/// <summary>
/// Spec 2026-11-09 «Worker y ejecución», calco de <c>CampaignDispatchWorker</c>: <c>PeriodicTimer</c> cada <c>ScheduledPollSeconds</c>, un scope
/// por fila y <see cref="Parallelism"/> envíos a la vez. Cada pasada recupera los leases vencidos, reclama hasta
/// <see cref="ScheduledMessageDispatcher.BatchSize"/> vencidos y los ejecuta, y una vez cada <c>PurgeIntervalHours</c> barre los adjuntos de las
/// filas terminales. Lo que decide cada paso vive en <see cref="ScheduledMessageDispatcher"/> y <see cref="ScheduledMessageExecutor"/>
/// (Application, probados con fakes); el SQL, en <see cref="ScheduledMessageStore"/>.
/// <para>
/// Cada fase corre en su propio scope y dentro de su propio <c>try</c>: una falla de la base en la recuperación se anota y no impide que los
/// vencidos salgan (ni al revés). Lo que quedó sin hacer lo toma la pasada siguiente: una fila recuperada a medias vuelve a vencer su lease.
/// </para>
/// </summary>
internal sealed partial class ScheduledMessageWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<MessagingWorkerOptions> options,
    TimeProvider time,
    ILogger<ScheduledMessageWorker> logger) : BackgroundService
{
    /// <summary>Cuántos programados se ejecutan a la vez. Cada uno puede subir un adjunto a Meta (hasta 5 minutos), así que uno solo en vuelo
    /// retrasaría al resto del lote.</summary>
    internal const int Parallelism = 4;

    private readonly Lock _purgeGate = new();

    // El timestamp de la última purga iniciada; null = nunca en este proceso, así que la primera pasada barre.
    private long? _lastPurgeAt;

    [LoggerMessage(Level = LogLevel.Error, Message = "Scheduled message tick failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Recovering expired scheduled messages failed; the next tick tries again.")]
    private static partial void LogRecoveryFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Claiming due scheduled messages failed; the next tick tries again.")]
    private static partial void LogClaimFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Executing scheduled message {ScheduledMessageId} threw; its lease expires and recovery closes it.")]
    private static partial void LogExecuteFailed(ILogger logger, Exception exception, Guid scheduledMessageId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Purging scheduled message media failed; the next tick tries again.")]
    private static partial void LogPurgeFailed(ILogger logger, Exception exception);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Value.ScheduledPollSeconds)));
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

    /// <summary>Una pasada: internal para las pruebas (P10). Sólo la cancelación del host sube; cualquier otra falla se anota por fase.</summary>
    internal async Task DrainAsync(CancellationToken cancellationToken)
    {
        // Antes del reclamo: un lease vencido que esta pasada cierra no compite con los vencidos nuevos por el lote.
        await RunPhaseAsync(dispatcher => dispatcher.RecoverExpiredAsync(cancellationToken), LogRecoveryFailed, cancellationToken);

        IReadOnlyList<ClaimedScheduledMessage> claimed = [];
        await RunPhaseAsync(async dispatcher => claimed = await dispatcher.ClaimDueAsync(cancellationToken), LogClaimFailed, cancellationToken);
        if (claimed.Count > 0)
        {
            // Como CampaignDispatchWorker (R3): el token del host sólo detiene lo que no empezó. Lo reclamado que no llegó a correr queda
            // Sending sin mensaje, y la recuperación lo cierra unknown_outcome al vencer el lease; nunca se reenvía solo.
            await Parallel.ForEachAsync(
                claimed,
                new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = cancellationToken },
                ExecuteOneAsync);
        }

        if (TryStartPurge())
        {
            await RunPhaseAsync(dispatcher => dispatcher.PurgeAsync(cancellationToken), LogPurgeFailed, cancellationToken);
        }
    }

    /// <summary>Spec «Purga»: un barrido diario. La marca se toma al empezar, así que una falla espera al intervalo siguiente en lugar de
    /// reintentar cada tick contra una base caída; lo que no se barrió sigue listado y nada se pierde.</summary>
    private bool TryStartPurge()
    {
        var interval = TimeSpan.FromHours(Math.Max(1, options.Value.PurgeIntervalHours));
        lock (_purgeGate)
        {
            if (_lastPurgeAt is { } last && time.GetElapsedTime(last) < interval)
            {
                return false;
            }

            _lastPurgeAt = time.GetTimestamp();
            return true;
        }
    }

    private async Task RunPhaseAsync(
        Func<ScheduledMessageDispatcher, Task> phase, Action<ILogger, Exception> logFailure, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            await phase(scope.ServiceProvider.GetRequiredService<ScheduledMessageDispatcher>());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logFailure(logger, exception);
        }
    }

    private async ValueTask ExecuteOneAsync(ClaimedScheduledMessage message, CancellationToken cancellationToken)
    {
        // Todo dentro del try, también crear y soltar el scope: una fila que falla no puede abortar al resto del lote (Parallel.ForEachAsync
        // deja de programar iteraciones ante una excepción).
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ScheduledMessageExecutor>().ExecuteAsync(message, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogExecuteFailed(logger, exception, message.Id);
        }
    }
}
