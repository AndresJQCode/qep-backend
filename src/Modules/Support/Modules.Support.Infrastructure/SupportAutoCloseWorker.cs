using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Support.Application;

namespace Modules.Support.Infrastructure;

/// <summary>
/// Fase 4b, Tarea 14 (H22): el cierre automático de los tickets Resueltos. Cada <see cref="SupportWorkerOptions.AutoCloseIntervalMinutes"/>
/// (15) recorre por páginas los tickets Resueltos hace más de un día y le pasa cada uno a <see cref="SupportAutoCloser"/> en su propio
/// scope, que decide con el reloj y el calendario de su tenant. No reclama filas: dos réplicas pueden revisar el mismo ticket, y la que
/// cierra segunda pierde por la versión del ticket (<c>RequestConcurrencyException</c>) y lo salta. Los Resueltos son pocos (todos se
/// cierran en días), así que el recorrido es corto.
/// </summary>
internal sealed partial class SupportAutoCloseWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<SupportWorkerOptions> options,
    ILogger<SupportAutoCloseWorker> logger) : BackgroundService
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "Support ticket {TicketId} changed while it was being auto-closed; it is skipped.")]
    private static partial void LogConcurrency(ILogger logger, Guid ticketId);

    // Sólo el id y el tipo de la excepción.
    [LoggerMessage(Level = LogLevel.Error, Message = "Auto-closing support ticket {TicketId} failed with {ExceptionType}; it is tried again on the next tick.")]
    private static partial void LogTicketFailed(ILogger logger, Guid ticketId, string exceptionType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Support auto-close tick failed; it runs again on the next interval.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, options.Value.AutoCloseIntervalMinutes)));
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                LogTickFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Un recorrido completo. Devuelve cuántos cerró.</summary>
    internal async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        const int take = SupportAutoCloser.DefaultBatchSize;
        SupportAutoCloseCandidate? after = null;
        var closed = 0;
        while (true)
        {
            IReadOnlyList<SupportAutoCloseCandidate> page;
            await using (var scope = scopeFactory.CreateAsyncScope())
            {
                page = await scope.ServiceProvider.GetRequiredService<SupportAutoCloser>()
                    .ListCandidatesAsync(take, after, cancellationToken);
            }

            foreach (var candidate in page)
            {
                if (await CloseAsync(candidate, cancellationToken))
                {
                    closed++;
                }
            }

            if (page.Count < take)
            {
                return closed;
            }

            after = page[^1];
        }
    }

    private async Task<bool> CloseAsync(SupportAutoCloseCandidate candidate, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<SupportAutoCloser>().CloseIfDueAsync(candidate, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RequestConcurrencyException)
        {
            LogConcurrency(logger, candidate.TicketId);
            return false;
        }
        catch (Exception exception)
        {
            LogTicketFailed(logger, candidate.TicketId, exception.GetType().Name);
            return false;
        }
    }
}
