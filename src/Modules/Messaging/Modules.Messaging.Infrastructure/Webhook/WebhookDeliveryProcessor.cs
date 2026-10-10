using BuildingBlocks.Application;
using Microsoft.Extensions.Logging;
using Modules.Messaging.Application;
using Modules.Messaging.Infrastructure.Persistence;

namespace Modules.Messaging.Infrastructure.Webhook;

internal enum DeliveryOutcome
{
    Processed,
    RetryLater,
}

/// <summary>§8.2: recorre <c>entry[].changes[]</c>; cada change es idempotente, así que reprocesar una
/// entrega a medias no duplica nada. Nunca registra el contenido de un mensaje.</summary>
internal sealed partial class WebhookDeliveryProcessor(
    MessagingDbContext dbContext,
    WebhookRouting routing,
    IClock clock,
    ILogger<WebhookDeliveryProcessor> logger)
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook delivery {DeliveryId}: {Count} inbound message(s) discarded for phone number id {PhoneNumberId} ({Reason}).")]
    private static partial void LogDiscarded(ILogger logger, long deliveryId, int count, string phoneNumberId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook delivery {DeliveryId}: field '{Field}' is not handled and was ignored.")]
    private static partial void LogIgnoredField(ILogger logger, long deliveryId, string field);

    public async Task<DeliveryOutcome> ProcessAsync(long deliveryId, string payload, int attempts, CancellationToken cancellationToken)
    {
        var outcome = DeliveryOutcome.Processed;
        foreach (var change in WebhookPayloadParser.Parse(payload))
        {
            switch (change)
            {
                case MessagesChange messages:
                    if (await ProcessMessagesAsync(deliveryId, messages, attempts, cancellationToken) == DeliveryOutcome.RetryLater)
                    {
                        outcome = DeliveryOutcome.RetryLater;
                    }

                    break;
                case AccountUpdateChange account:
                    await ProcessAccountUpdateAsync(deliveryId, account, cancellationToken);
                    break;
                case UnknownChange unknown:
                    LogIgnoredField(logger, deliveryId, unknown.Field);
                    break;
                default:
                    break;
            }
        }

        return outcome;
    }

    private async Task<DeliveryOutcome> ProcessMessagesAsync(long deliveryId, MessagesChange change, int attempts, CancellationToken cancellationToken)
    {
        var route = await routing.FindRouteAsync(change.PhoneNumberId, cancellationToken);
        if (route is null)
        {
            LogDiscarded(logger, deliveryId, change.Messages.Count, change.PhoneNumberId, "unknown-route");
            return DeliveryOutcome.Processed;
        }

        // Decisión del owner (D-M18): con Paused o el módulo apagado los entrantes se descartan.
        var accepting = !route.IsPaused && await routing.IsModuleEnabledAsync(route.TenantId, cancellationToken);
        if (!accepting && change.Messages.Count > 0)
        {
            LogDiscarded(logger, deliveryId, change.Messages.Count, change.PhoneNumberId, route.IsPaused ? "paused" : "module-off");
        }

        if (accepting)
        {
            var now = clock.UtcNow;
            foreach (var message in change.Messages)
            {
                await InboundIngestion.IngestAsync(dbContext, route.TenantId, route.ConnectionId, message, now, cancellationToken);
            }
        }

        // Los statuses se aplican siempre, también con Paused o módulo apagado (decisión 7, D-M18). Task 13b.
        return await ProcessStatusesAsync(deliveryId, route, change, attempts, cancellationToken);
    }

    /// <summary>Costura de la Task 13b (§7.5, «Estados»): por ahora no aplica nada y deja la entrega procesada.</summary>
    private static Task<DeliveryOutcome> ProcessStatusesAsync(long deliveryId, MessagingRoute route, MessagesChange change, int attempts, CancellationToken cancellationToken) =>
        Task.FromResult(DeliveryOutcome.Processed);

    /// <summary>Costura de la Task 13b (§8.2, <c>account_update</c>).</summary>
    private static Task ProcessAccountUpdateAsync(long deliveryId, AccountUpdateChange change, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
