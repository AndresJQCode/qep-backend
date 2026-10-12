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
    ILogger<CampaignDispatchWorker> logger) : BackgroundService
{
    /// <summary>Cuántos envíos corren a la vez. El ritmo lo fija el cubo, no esto: con ~300 ms por llamada a Meta, 20 mps piden más de uno
    /// en vuelo, y los tokens esperan por turno.</summary>
    internal const int Parallelism = 8;

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

    /// <summary>Una pasada: internal para las pruebas (P10). Una falla en un paso la deja subir; la del envío de un destinatario sólo se anota.</summary>
    internal async Task DrainAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ClaimedRecipient> claimed;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<CampaignDispatcher>();
            await dispatcher.PrepareTickAsync(cancellationToken);
            claimed = await dispatcher.ClaimAsync(campaignOptions.Value.MessagesPerSecond, cancellationToken);
        }

        if (claimed.Count > 0)
        {
            // R3: el token del host sólo detiene lo que todavía no empezó (el reclamo, los destinatarios sin turno, la espera del ritmo).
            // Parallel.ForEachAsync no vuelve hasta que terminan los envíos que ya están corriendo, y éstos llaman a Meta con
            // CancellationToken.None y liquidan igual: al cerrar el host, lo que está en vuelo se asienta en lugar de quedar unknown_outcome.
            // Lo que no llegó a empezar queda Sending sin mensaje, y la recuperación lo devuelve a Pending al vencer el lease.
            await Parallel.ForEachAsync(
                claimed,
                new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = cancellationToken },
                SendOneAsync);
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
