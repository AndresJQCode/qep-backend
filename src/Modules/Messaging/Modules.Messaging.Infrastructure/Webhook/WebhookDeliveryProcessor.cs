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
    IMessagingConnectionDirectory directory,
    IClock clock,
    ILogger<WebhookDeliveryProcessor> logger)
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook delivery {DeliveryId}: a status with a QEP callback has no message row yet (attempt {Attempt}); it is retried.")]
    private static partial void LogStatusPending(ILogger logger, long deliveryId, int attempt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook delivery {DeliveryId}: a status without a QEP callback matched no message and was discarded.")]
    private static partial void LogForeignStatus(ILogger logger, long deliveryId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook delivery {DeliveryId}: account_update {Event} (ban state {BanState}); disables connections: {Disables}.")]
    private static partial void LogAccountEvent(ILogger logger, long deliveryId, string @event, string? banState, bool disables);

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

    /// <summary>§8.2: un status con callback de QEP y sin fila queda pendiente (el envío puede no haber
    /// commiteado el wamid); sin callback y sin fila es ajeno (la app del teléfono) y se descarta.</summary>
    private async Task<DeliveryOutcome> ProcessStatusesAsync(long deliveryId, MessagingRoute route, MessagesChange change, int attempts, CancellationToken cancellationToken)
    {
        var outcome = DeliveryOutcome.Processed;
        foreach (var status in change.Statuses)
        {
            var result = await StatusIngestion.ApplyAsync(dbContext, route.ConnectionId, status, cancellationToken);
            if (result == StatusOutcome.NotFound && status.CallbackMessageId is not null)
            {
                LogStatusPending(logger, deliveryId, attempts);
                outcome = DeliveryOutcome.RetryLater;
            }
            else if (result == StatusOutcome.NotFound)
            {
                LogForeignStatus(logger, deliveryId);
            }
        }

        return outcome;
    }

    /// <summary>§8.2, D-M16: cuenta deshabilitada, borrada, app desinstalada u offboarded → account_disabled
    /// en cada conexión de la WABA (sólo las Active cambian; el reporter ignora el resto). D-M12: REINSTATE
    /// y ACCOUNT_RECONNECTED no reactivan.</summary>
    private async Task ProcessAccountUpdateAsync(long deliveryId, AccountUpdateChange change, CancellationToken cancellationToken)
    {
        var disabled = change.Event switch
        {
            "DISABLED_UPDATE" => string.Equals(change.BanState, "DISABLE", StringComparison.Ordinal),
            "ACCOUNT_DELETED" or "PARTNER_REMOVED" or "PARTNER_APP_UNINSTALLED" or "ACCOUNT_OFFBOARDED" => true,
            _ => false,
        };
        LogAccountEvent(logger, deliveryId, change.Event, change.BanState, disabled);
        if (!disabled)
        {
            return;
        }

        // Directo al directorio, sin la caché de WebhookRouting: el estado de la conexión tiene que ser el de ahora.
        foreach (var route in await directory.FindByAccountAsync(change.WabaId, cancellationToken))
        {
            // ConnectionFailureCodes.AccountDisabled de Integrations, como literal: Messaging no lo referencia.
            await directory.ReportRejectedAsync(route.TenantId, route.ConnectionId, "account_disabled", cancellationToken);
        }
    }
}
