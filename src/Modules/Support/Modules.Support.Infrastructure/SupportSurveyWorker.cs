using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Support.Application;

namespace Modules.Support.Infrastructure;

/// <summary>
/// Fase 4b, Tarea 11 (H16): manda las encuestas de satisfacción pendientes. Espera el timbre de su cola (<c>support_survey</c>) hasta la red
/// de seguridad o, sin timbres, <see cref="SupportWorkerOptions.SurveyIntervalSeconds"/>. Por tick reclama un lote
/// (<c>FOR UPDATE SKIP LOCKED</c> + <c>locked_until</c>: varias réplicas no toman la misma) y procesa cada encuesta en su propio scope,
/// renovando su reclamo justo antes de mandarla: el reclamo cubre un envío, no el lote. Una que lanza pasa a su reintento en un scope
/// nuevo (1, 5 y 15 minutos y después <c>Failed</c>). Un lote lleno se repite sin esperar.
/// </summary>
internal sealed partial class SupportSurveyWorker(
    IQueueDoorbell doorbell,
    IServiceScopeFactory scopeFactory,
    IOptions<SupportWorkerOptions> options,
    ILogger<SupportSurveyWorker> logger) : BackgroundService
{
    // Sólo el id y el tipo de la excepción: el mensaje de un canal puede traer datos del solicitante.
    [LoggerMessage(Level = LogLevel.Error, Message = "Support survey {SurveyId} failed with {ExceptionType}; it goes to its next retry.")]
    private static partial void LogSurveyFailed(ILogger logger, Guid surveyId, string exceptionType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Support survey {SurveyId} could not be moved to its retry ({ExceptionType}); it is claimed again when its claim expires.")]
    private static partial void LogRetryFailed(ILogger logger, Guid surveyId, string exceptionType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Support survey tick failed; it runs again on the next interval.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        var take = Math.Max(1, settings.SurveyBatchSize);
        using var listener = doorbell.Listen(DoorbellQueues.SupportSurvey);
        await DoorbellPolling.RunAsync(
            listener,
            TimeSpan.FromSeconds(Math.Max(1, settings.SurveyIntervalSeconds)),
            DoorbellQueues.SafetyNet(DoorbellQueues.SupportSurvey),
            async _ =>
            {
                try
                {
                    return await RunOnceAsync(take, stoppingToken) == take;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    LogTickFailed(logger, exception);
                    return false;
                }
            },
            stoppingToken);
    }

    /// <summary>Un lote: el reclamo y cada encuesta en su scope. Devuelve cuántas reclamó.</summary>
    internal async Task<int> RunOnceAsync(int take, CancellationToken cancellationToken)
    {
        SupportSurveyBatch batch;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            batch = await scope.ServiceProvider.GetRequiredService<SupportSurveySender>().ClaimAsync(take, cancellationToken);
        }

        foreach (var surveyId in batch.SurveyIds)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<SupportSurveySender>()
                    .ProcessAsync(surveyId, batch.ClaimedUntil, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogSurveyFailed(logger, surveyId, exception.GetType().Name);
                await RetryAfterFailureAsync(surveyId, cancellationToken);
            }
        }

        return batch.SurveyIds.Count;
    }

    // Revisión del bloque C: en un scope nuevo, la encuesta que lanzó pasa a su reintento (y al final a Failed). Si ni eso se puede, queda
    // reclamada y vuelve a la cola al vencer el reclamo, como antes.
    private async Task RetryAfterFailureAsync(Guid surveyId, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<SupportSurveySender>().RetryAfterFailureAsync(surveyId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogRetryFailed(logger, surveyId, exception.GetType().Name);
        }
    }
}
