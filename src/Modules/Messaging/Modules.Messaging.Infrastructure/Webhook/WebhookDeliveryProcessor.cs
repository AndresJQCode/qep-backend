using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
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
    IMessagingCustomerDirectory customers,
    IMessagingAssignees assignees,
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

    // Spec 2026-10-10 §8.1: «se salta y se registra». Sin el texto, el teléfono ni el BSUID que no vino.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook delivery {DeliveryId}: {Count} inbound message(s) without a valid from_user_id were skipped for phone number id {PhoneNumberId}.")]
    private static partial void LogSkippedWithoutUserId(ILogger logger, long deliveryId, int count, string phoneNumberId);

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
                case UserIdUpdateChange:
                    // Spec 2026-10-10 §8.3: se parsea desde ya; aplicarlo llega con la T10. Hasta entonces, al log.
                    LogIgnoredField(logger, deliveryId, "user_id_update");
                    break;
                default:
                    break;
            }
        }

        return outcome;
    }

    private async Task<DeliveryOutcome> ProcessMessagesAsync(long deliveryId, MessagesChange change, int attempts, CancellationToken cancellationToken)
    {
        if (change.SkippedWithoutUserId > 0)
        {
            LogSkippedWithoutUserId(logger, deliveryId, change.SkippedWithoutUserId, change.PhoneNumberId);
        }

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
                var context = await PrepareAsync(route.TenantId, route.ConnectionId, message, cancellationToken);
                await InboundIngestion.IngestAsync(dbContext, route.TenantId, route.ConnectionId, message, context, now, cancellationToken);
            }
        }

        // Los statuses se aplican siempre, también con Paused o módulo apagado (decisión 7, D-M18). Task 13b.
        return await ProcessStatusesAsync(deliveryId, route, change, attempts, cancellationToken);
    }

    /// <summary>Spec 2026-10-10 §8.1, «Antes de la transacción»: con conversación y cliente no se llama a Customers. Si
    /// no hay conversación, el asignado a heredar (§8.2, P16): la más reciente del cliente con asignado, si todavía puede
    /// responder.</summary>
    private async Task<InboundContext> PrepareAsync(Guid tenantId, Guid connectionId, InboundMessage message, CancellationToken cancellationToken)
    {
        var existing = await dbContext.Database.SqlQuery<ExistingConversation>(
            $"""SELECT id AS "Id", customer_id AS "CustomerId" FROM messaging.conversations WHERE connection_id = {connectionId} AND user_id = {message.UserId}""")
            .ToListAsync(cancellationToken);
        if (existing is [{ CustomerId: not null }])
        {
            return InboundContext.None;
        }

        var customer = await customers.EnsureAsync(
            tenantId, new MessagingContact(message.UserId, message.WaId, message.ProfileName, message.Username), cancellationToken);
        Guid? heir = null;
        if (existing.Count == 0)
        {
            var candidates = await dbContext.Database.SqlQuery<Guid>(
                $"""
                SELECT assigned_member_id AS "Value" FROM messaging.conversations
                WHERE tenant_id = {tenantId} AND customer_id = {customer.CustomerId} AND assigned_member_id IS NOT NULL
                ORDER BY last_activity_at DESC
                LIMIT 1
                """).ToListAsync(cancellationToken);
            if (candidates is [var candidate] && await assignees.CanReplyAsync(tenantId, candidate, cancellationToken))
            {
                heir = candidate;
            }
        }

        return new InboundContext(customer.CustomerId, customer.Created, heir);
    }

    private sealed record ExistingConversation(Guid Id, Guid? CustomerId);

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
