using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Messaging.Application;
using Modules.Messaging.Infrastructure.Options;

namespace Modules.Messaging.Infrastructure.Campaigns;

/// <summary>
/// Spec 2026-10-11 §6.3: copia el patrón de <c>MediaCopyWorker</c> —<c>PeriodicTimer</c> (2 s por defecto), un scope por lote y reclamos
/// con <c>FOR UPDATE SKIP LOCKED</c> y lease—, de modo que varias réplicas pueden correrlo a la vez y cada destinatario tiene un solo
/// ganador. Cada tick hace los cinco pasos: activar las programadas, congelar la audiencia, enviar hasta <c>MessagesPerSecond × 2</c>,
/// recuperar los leases vencidos y cerrar las campañas sin pendientes. Lo que decide cada paso vive en <see cref="CampaignDispatcher"/>
/// (Application, probado con fakes); acá sólo el ciclo, los scopes y el paralelismo. Un envío por scope: un <c>DbContext</c> no es seguro entre hilos.
/// </summary>
internal sealed partial class CampaignDispatchWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<MessagingWorkerOptions> workerOptions,
    IOptions<MessagingCampaignOptions> campaignOptions,
    TimeProvider time,
    ILogger<CampaignDispatchWorker> logger) : BackgroundService
{
    /// <summary>Cuántos envíos corren a la vez. El ritmo lo fija el presupuesto compartido, no esto: con ~300 ms por llamada a Meta, 20 mps piden
    /// más de uno en vuelo.</summary>
    internal const int Parallelism = 8;

    /// <summary>Lo mínimo que espera el worker cuando el presupuesto no alcanzó para todos: pedir token por token multiplicaría las vueltas a la
    /// base sin mandar más (la capacidad del cubo es un segundo, así que 250 ms no pierde ninguno).</summary>
    internal static readonly TimeSpan MinimumPause = TimeSpan.FromMilliseconds(250);

    [LoggerMessage(Level = LogLevel.Error, Message = "Campaign dispatch tick failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Sending campaign recipient {RecipientId} threw; its lease expires and recovery decides what happens next.")]
    private static partial void LogSendFailed(ILogger logger, Exception exception, Guid recipientId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, workerOptions.Value.CampaignPollSeconds)));
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

    /// <summary>Una pasada: internal para las pruebas (P10). Una falla en un paso la deja subir; la del envío de un destinatario sólo se anota.
    /// Decisión del owner 2026-10-12 (P3): el cubo de la conexión guarda un segundo de ráfaga, así que un tick cada 2 s no alcanzaría el ritmo.
    /// La pasada reclama y envía en rondas, esperando lo que el presupuesto indique, hasta gastar un intervalo del worker; ahí corren la
    /// recuperación y el cierre y el siguiente tick arranca enseguida.</summary>
    internal async Task DrainAsync(CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        var span = TimeSpan.FromSeconds(Math.Max(1, workerOptions.Value.CampaignPollSeconds));
        var first = true;
        while (true)
        {
            IReadOnlyList<ClaimedRecipient> claimed;
            CampaignAdmission admission;
            await using (var scope = scopeFactory.CreateAsyncScope())
            {
                var dispatcher = scope.ServiceProvider.GetRequiredService<CampaignDispatcher>();
                if (first)
                {
                    await dispatcher.PrepareTickAsync(cancellationToken);
                    first = false;
                }

                claimed = await dispatcher.ClaimAsync(campaignOptions.Value.MessagesPerSecond, cancellationToken);
                admission = await dispatcher.AdmitAsync(claimed, cancellationToken);
            }

            if (admission.Admitted.Count > 0)
            {
                // R3: el token del host sólo detiene lo que todavía no empezó (el reclamo, los destinatarios sin turno). Parallel.ForEachAsync
                // no vuelve hasta que terminan los envíos que ya están corriendo, y éstos llaman a Meta con CancellationToken.None y liquidan
                // igual: al cerrar el host, lo que está en vuelo se asienta en lugar de quedar unknown_outcome. Lo que no llegó a empezar
                // queda Sending sin mensaje, y la recuperación lo devuelve a Pending al vencer el lease.
                await Parallel.ForEachAsync(
                    admission.Admitted,
                    new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = cancellationToken },
                    SendOneAsync);
            }

            var remaining = span - time.GetElapsedTime(started);
            if (claimed.Count == 0 || remaining <= TimeSpan.Zero)
            {
                break;
            }

            if (admission.Wait is { } wait)
            {
                var pause = wait > MinimumPause ? wait : MinimumPause;
                if (pause >= remaining)
                {
                    break;
                }

                await Task.Delay(pause, time, cancellationToken);
            }
        }

        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<CampaignDispatcher>().FinishTickAsync(cancellationToken);
        }
    }

    private async ValueTask SendOneAsync(ClaimedRecipient recipient, CancellationToken cancellationToken)
    {
        // Todo dentro del try, también crear y soltar el scope: un destinatario que falla no puede abortar al resto del lote (Parallel.ForEachAsync
        // deja de programar iteraciones ante una excepción, y los reclamados que no llegaron a correr quedarían con su lease hasta vencer).
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<CampaignDispatcher>().SendAsync(recipient, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogSendFailed(logger, exception, recipient.Id);
        }
    }
}
