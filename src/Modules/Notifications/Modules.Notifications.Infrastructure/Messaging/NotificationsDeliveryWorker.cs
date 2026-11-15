using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Modules.Notifications.Infrastructure.Messaging;

/// <summary>
/// El único hosted service de los correos (spec 2026-11-13, D-7; plan P13): en cada vuelta drena, en secuencia, cada
/// consumidor del outbox (<see cref="OutboxDeliveryWorker"/>, trece al 2026-11-13). Antes eran trece hosted services con
/// su propio sondeo cada 15 s: trece consultas en reposo contra la base por pod. El reclamo, el lease y el aislamiento por
/// mensaje siguen en cada definición; acá sólo vive el bucle.
/// </summary>
internal sealed partial class NotificationsDeliveryWorker(
    IEnumerable<OutboxDeliveryWorker> definitions,
    ILogger<NotificationsDeliveryWorker> logger) : BackgroundService
{
    private readonly IReadOnlyList<OutboxDeliveryWorker> _definitions = [.. definitions];

    [LoggerMessage(Level = LogLevel.Error, Message = "Delivery tick of {Consumer} failed.")]
    private static partial void LogTickFailed(ILogger logger, string consumer, Exception exception);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(OutboxDeliveryWorker.PollInterval);
        do
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    /// Una vuelta: cada definición una vez, en secuencia. Una que lanza se loguea con su consumidor y sigue la siguiente
    /// (antes cada worker tenía su propio bucle y una falla no tocaba a los demás). Sólo el apagado sale (A4).
    /// </summary>
    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        foreach (var definition in _definitions)
        {
            try
            {
                await definition.DrainAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogTickFailed(logger, definition.ConsumerName, exception);
            }
        }
    }
}
