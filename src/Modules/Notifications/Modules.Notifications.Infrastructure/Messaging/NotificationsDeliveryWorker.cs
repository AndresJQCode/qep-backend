using BuildingBlocks.Application;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Modules.Notifications.Infrastructure.InApp;

namespace Modules.Notifications.Infrastructure.Messaging;

/// <summary>
/// El único hosted service de los correos (spec 2026-11-13, D-7; plan P13): en cada vuelta drena, en secuencia, cada
/// consumidor del outbox (<see cref="OutboxDeliveryWorker"/>, trece al 2026-11-13). Antes eran trece hosted services con
/// su propio sondeo cada 15 s: trece consultas en reposo contra la base por pod. El reclamo, el lease y el aislamiento por
/// mensaje siguen en cada definición; acá sólo vive el bucle.
/// </summary>
internal sealed partial class NotificationsDeliveryWorker(
    IQueueDoorbell doorbell,
    IEnumerable<OutboxDeliveryWorker> definitions,
    IEnumerable<InAppOutboxConsumer> inAppConsumers,
    ILogger<NotificationsDeliveryWorker> logger) : BackgroundService
{
    private readonly IReadOnlyList<OutboxDeliveryWorker> _definitions = [.. definitions];

    // Spec 2026-11-22 §12: the bell's consumers share this loop and its outbox doorbell; they are not hosted services of their own.
    private readonly IReadOnlyList<InAppOutboxConsumer> _inAppConsumers = [.. inAppConsumers];

    [LoggerMessage(Level = LogLevel.Error, Message = "Delivery tick of {Consumer} failed.")]
    private static partial void LogTickFailed(ILogger logger, string consumer, Exception exception);

    /// <summary>
    /// Spec 2026-11-13 (Valkey), D-7: espera el timbre de <c>outbox</c> hasta su red de seguridad (30 s); con el área apagada o Valkey
    /// caído, <see cref="OutboxDeliveryWorker.PollInterval"/> como antes.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var listener = doorbell.Listen(DoorbellQueues.Outbox);
        await DoorbellPolling.RunAsync(
            listener,
            OutboxDeliveryWorker.PollInterval,
            DoorbellQueues.SafetyNet(DoorbellQueues.Outbox),
            TickAsync,
            stoppingToken);
    }

    /// <summary>
    /// Una vuelta: cada definición una vez, en secuencia. Una que lanza se loguea con su consumidor y sigue la siguiente
    /// (antes cada worker tenía su propio bucle y una falla no tocaba a los demás). Sólo el apagado sale (A4). Devuelve
    /// <c>true</c> si algún consumidor trajo su lote lleno: el bucle repite sin esperar.
    /// </summary>
    internal async Task<bool> TickAsync(CancellationToken cancellationToken)
    {
        var more = false;
        foreach (var definition in _definitions)
        {
            try
            {
                more |= await definition.DrainAsync(cancellationToken);
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

        foreach (var consumer in _inAppConsumers)
        {
            try
            {
                more |= await consumer.DrainAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogTickFailed(logger, consumer.ConsumerName, exception);
            }
        }

        return more;
    }
}
