using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Modules.Shopify.Infrastructure.Messaging;

namespace Modules.Shopify.Infrastructure.Workers;

// Spec 2026-10-26 D-C6: cada 10 s, como el consumidor de eventos de cotización desde que develop relajó los sondeos (e7ea2083). El
// sondeo es costo en reposo contra una base compartida, pero acá lo espera una persona: la consola del operador pide el primer
// avance de un sinónimo nuevo cada 60 s, y después de sembrar todavía corre el worker que corrige. No subir de ~15 s sin mirar ese
// presupuesto. Sólo el apagado del host lo detiene; un tick fallido se registra y el siguiente reintenta.
internal sealed partial class CitySynonymEventsWorker(CitySynonymEventsProcessor processor, ILogger<CitySynonymEventsWorker> logger) : BackgroundService
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    [LoggerMessage(Level = LogLevel.Error, Message = "Shopify city synonym events tick failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try
            {
                await processor.ProcessBatchAsync(stoppingToken);
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
}
