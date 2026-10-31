using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using Modules.Messaging.Infrastructure.Options;
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
    IInstagramClient instagram,
    IClock clock,
    MarketingOptOutButtons optOutButtons,
    IMessagingNotifier notifier,
    MessagingMetrics metrics,
    IOptions<MessagingAutomationOptions> automationOptions,
    ILogger<WebhookDeliveryProcessor> logger)
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook delivery {DeliveryId}: the realtime notification for conversation {ConversationId} failed; the delivery is processed anyway.")]
    private static partial void LogNotifyFailed(ILogger logger, long deliveryId, Guid conversationId, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook delivery {DeliveryId}: a status with a QEP callback has no message row yet (attempt {Attempt}); it is retried.")]
    private static partial void LogStatusPending(ILogger logger, long deliveryId, int attempt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook delivery {DeliveryId}: a status without a QEP callback matched no message and was discarded.")]
    private static partial void LogForeignStatus(ILogger logger, long deliveryId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook delivery {DeliveryId}: template {MetaTemplateId} is now {Event} (reason {Reason}) in tenant {TenantId}, connection {ConnectionId}.")]
    private static partial void LogTemplateStatus(ILogger logger, long deliveryId, string metaTemplateId, string @event, string? reason, Guid tenantId, Guid connectionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook delivery {DeliveryId}: template {MetaTemplateId} was flagged in tenant {TenantId}, connection {ConnectionId}; it stays sendable and its status is not changed.")]
    private static partial void LogTemplateFlagged(ILogger logger, long deliveryId, string metaTemplateId, Guid tenantId, Guid connectionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook delivery {DeliveryId}: no mirror row for meta_template_id {MetaTemplateId} in tenant {TenantId}, connection {ConnectionId}; the next refresh will bring it.")]
    private static partial void LogTemplateWithoutRow(ILogger logger, long deliveryId, string metaTemplateId, Guid tenantId, Guid connectionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook delivery {DeliveryId}: account_update {Event} (ban state {BanState}); disables connections: {Disables}.")]
    private static partial void LogAccountEvent(ILogger logger, long deliveryId, string @event, string? banState, bool disables);

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook delivery {DeliveryId}: {Count} inbound message(s) discarded for phone number id {PhoneNumberId} ({Reason}).")]
    private static partial void LogDiscarded(ILogger logger, long deliveryId, int count, string phoneNumberId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook delivery {DeliveryId}: field '{Field}' is not handled and was ignored.")]
    private static partial void LogIgnoredField(ILogger logger, long deliveryId, string field);

    // Spec 2026-10-10 §8.1: «se salta y se registra». Sin el texto, el teléfono ni el BSUID que no vino.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook delivery {DeliveryId}: {Count} inbound message(s) without a valid from_user_id were skipped for phone number id {PhoneNumberId}.")]
    private static partial void LogSkippedWithoutUserId(ILogger logger, long deliveryId, int count, string phoneNumberId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook delivery {DeliveryId}: the marketing opt-out by button could not be recorded; the message was ingested anyway.")]
    private static partial void LogButtonOptOutFailed(ILogger logger, long deliveryId, Exception exception);

    // Spec 2026-10-29: sin el teléfono ni el texto; sólo la conexión.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook delivery {DeliveryId}: history for connection {ConnectionId} has no history sync row; its state was not recorded.")]
    private static partial void LogHistoryWithoutSync(ILogger logger, long deliveryId, Guid connectionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook delivery {DeliveryId}: {Count} history message(s) without id or timestamp were skipped for connection {ConnectionId}.")]
    private static partial void LogHistorySkipped(ILogger logger, long deliveryId, int count, Guid connectionId);

    public Task<DeliveryOutcome> ProcessAsync(long deliveryId, string payload, int attempts, CancellationToken cancellationToken) =>
        ProcessAsync(deliveryId, WebhookProvider.WhatsApp, payload, attempts, cancellationToken);

    /// <summary>Spec 2026-10-14 §6.2: la entrega sabe de qué producto vino (<c>provider</c>) y eso decide el parser.</summary>
    public Task<DeliveryOutcome> ProcessAsync(long deliveryId, WebhookProvider provider, string payload, int attempts, CancellationToken cancellationToken) =>
        provider == WebhookProvider.Instagram
            ? ProcessInstagramAsync(deliveryId, payload, cancellationToken)
            : ProcessWhatsAppAsync(deliveryId, payload, attempts, cancellationToken);

    private async Task<DeliveryOutcome> ProcessWhatsAppAsync(long deliveryId, string payload, int attempts, CancellationToken cancellationToken)
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
                case TemplateStatusChange templateStatus:
                    await ProcessTemplateStatusAsync(deliveryId, templateStatus, cancellationToken);
                    break;
                case UnknownChange unknown:
                    LogIgnoredField(logger, deliveryId, unknown.Field);
                    break;
                case UserIdUpdateChange update:
                    await ProcessUserIdUpdateAsync(deliveryId, update, cancellationToken);
                    break;
                case HistoryChange history:
                    await ProcessHistoryAsync(deliveryId, history, cancellationToken);
                    break;
                case SmbEchoesChange echoes:
                    await ProcessEchoesAsync(deliveryId, echoes, cancellationToken);
                    break;
                case ContactsSyncChange contacts:
                    await ProcessContactsAsync(deliveryId, contacts, cancellationToken);
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

        // Spec 2026-10-10 §8.3, P7: el cambio de número se aplica siempre, también con Paused o el módulo apagado, y
        // **antes** que los entrantes del mismo change: así el primer mensaje con el BSUID nuevo cae en la conversación
        // movida en vez de abrir otra (D-A7 queda para cuando el mensaje llegó en una entrega anterior).
        if (change.NumberChanges.Count > 0)
        {
            var occurredAt = NumberChangeOccurredAt(change.Messages);
            foreach (var numberChange in change.NumberChanges)
            {
                await ApplyNumberChangeAsync([route], numberChange, occurredAt, cancellationToken);
            }
        }

        if (accepting)
        {
            var now = clock.UtcNow;
            // Spec 2026-10-21 D-M1: los mensajes de un change comparten `now`, y el orden en que el motor los procesa sale de created_at
            // de la cola. El bucle recorre el orden en que Meta los mandó, así que un microsegundo por posición (la resolución de
            // timestamptz) da una clave estrictamente creciente sin depender de los UUID v7 del mismo milisegundo. La ingesta aplica
            // el corrimiento sólo a la fila de la cola.
            var position = 0;
            foreach (var message in change.Messages)
            {
                var context = await PrepareAsync(route.TenantId, route.ConnectionId, message, cancellationToken);
                var ingested = await InboundIngestion.IngestAsync(dbContext, route.TenantId, route.ConnectionId, message, context, now, automationOptions.Value.Enqueue, cancellationToken, position: position++);
                if (ingested is not null)
                {
                    // Sólo el entrante nuevo mide: un reenvío de Meta (null) no. Antes de NotifyAsync, que puede fallar; el reloj se toma aquí
                    // porque `now` es de antes del bucle.
                    metrics.RecordInboundLag("whatsapp", message.OccurredAt, clock.UtcNow);
                    // Spec 2026-10-14: ya commiteado; un reenvío de Meta (null) no avisa nada.
                    await NotifyAsync(deliveryId, ingested.ConversationId,
                        () => notifier.InboundMessageAsync(route.TenantId, ingested.ConversationId, ingested.MessageId, ingested.AssignedMemberId, cancellationToken));
                }

                await MarkOptOutIfButtonAsync(deliveryId, route, message, cancellationToken);
            }
        }

        // Los statuses se aplican siempre, también con Paused o módulo apagado (decisión 7, D-M18). Task 13b.
        return await ProcessStatusesAsync(deliveryId, route, change, attempts, cancellationToken);
    }

    /// <summary>
    /// Spec 2026-10-29 §4.1 y §4.4: el historial de la app. Se procesa también con la conexión <c>Paused</c> o el módulo apagado (D-5):
    /// no se puede volver a pedir y no envía ni dispara nada. Por cada hilo, el cliente por teléfono (D-10) fuera de la transacción de
    /// Messaging, como la ingesta en vivo, y después el hilo en su propia transacción; al final de cada parte, su progreso. Todo es
    /// idempotente, así que reprocesar una entrega a medias no duplica nada.
    /// </summary>
    private async Task ProcessHistoryAsync(long deliveryId, HistoryChange change, CancellationToken cancellationToken)
    {
        var route = await routing.FindRouteAsync(change.PhoneNumberId, cancellationToken);
        if (route is null)
        {
            var count = change.Chunks.Sum(chunk => chunk.Threads.Sum(thread => thread.Messages.Count));
            LogDiscarded(logger, deliveryId, count, change.PhoneNumberId, "unknown-route");
            return;
        }

        var now = clock.UtcNow;
        if (change.ErrorCodes.Contains(WebhookPayloadParser.HistoryDeclinedCode)
            && await CoexistenceIngestion.RecordDeclinedAsync(dbContext, route.ConnectionId, now, cancellationToken) == 0)
        {
            LogHistoryWithoutSync(logger, deliveryId, route.ConnectionId);
        }

        if (change.Chunks.Count == 0)
        {
            return;
        }

        var connectedAt = (await dbContext.Database.SqlQuery<DateTimeOffset>(
            $"""SELECT connected_at AS "Value" FROM messaging.history_syncs WHERE connection_id = {route.ConnectionId}""")
            .ToListAsync(cancellationToken)) is [var at] ? at : (DateTimeOffset?)null;
        foreach (var chunk in change.Chunks)
        {
            if (chunk.SkippedMessages > 0)
            {
                LogHistorySkipped(logger, deliveryId, chunk.SkippedMessages, route.ConnectionId);
            }

            foreach (var thread in chunk.Threads.Where(thread => thread.Messages.Count > 0))
            {
                var customer = await customers.EnsureByPhoneAsync(route.TenantId, thread.WaId, null, cancellationToken);
                await CoexistenceIngestion.IngestThreadAsync(
                    dbContext, route.TenantId, route.ConnectionId, thread.WaId, thread.Messages, customer.CustomerId, CoexistenceSource.History,
                    connectedAt, now, cancellationToken);
            }

            if (await CoexistenceIngestion.RecordChunkAsync(dbContext, route.ConnectionId, chunk, now, cancellationToken) == 0)
            {
                LogHistoryWithoutSync(logger, deliveryId, route.ConnectionId);
            }
        }
    }

    /// <summary>
    /// Spec 2026-10-29 §4.4: lo que el negocio escribe desde la app. Con la conexión <c>Paused</c> o el módulo apagado también se
    /// procesa (D-5): ya ocurrió y no envía nada. Los ecos seguidos del mismo teléfono van en una sola llamada. No suma no leídos, no
    /// toca la ventana ni el bot y no avisa en tiempo real.
    /// </summary>
    private async Task ProcessEchoesAsync(long deliveryId, SmbEchoesChange change, CancellationToken cancellationToken)
    {
        var route = await routing.FindRouteAsync(change.PhoneNumberId, cancellationToken);
        if (route is null)
        {
            LogDiscarded(logger, deliveryId, change.Echoes.Count, change.PhoneNumberId, "unknown-route");
            return;
        }

        var now = clock.UtcNow;
        var index = 0;
        while (index < change.Echoes.Count)
        {
            var waId = change.Echoes[index].WaId;
            var group = new List<CoexistenceMessage>();
            while (index < change.Echoes.Count && change.Echoes[index].WaId == waId)
            {
                group.Add(change.Echoes[index++].Message);
            }

            var customer = await customers.EnsureByPhoneAsync(route.TenantId, waId, null, cancellationToken);
            await CoexistenceIngestion.IngestThreadAsync(
                dbContext, route.TenantId, route.ConnectionId, waId, group, customer.CustomerId, CoexistenceSource.Echo, null, now, cancellationToken);
        }
    }

    /// <summary>Spec 2026-10-29 §8: los contactos de la app del negocio entran como clientes incompletos, sin BSUID. Un contacto quitado
    /// se ignora (D-7): borrarlo en la app no borra al cliente de QEP.</summary>
    private async Task ProcessContactsAsync(long deliveryId, ContactsSyncChange change, CancellationToken cancellationToken)
    {
        var route = await routing.FindRouteAsync(change.PhoneNumberId, cancellationToken);
        if (route is null)
        {
            LogDiscarded(logger, deliveryId, change.Contacts.Count, change.PhoneNumberId, "unknown-route");
            return;
        }

        foreach (var contact in change.Contacts.Where(contact => !contact.Removed))
        {
            await customers.EnsureByPhoneAsync(route.TenantId, contact.WaId, contact.FullName ?? contact.FirstName, cancellationToken);
        }
    }

    /// <summary>Decisión del owner 2026-10-12 (P1): ya con el mensaje commiteado, si es el botón de respuesta rápida «Detener promociones» y la
    /// conversación tiene cliente, se apaga su marketing con origen <c>button</c>. Idempotente (un reenvío de Meta repite sin efecto). Un
    /// fallo se registra y no se relanza: el mensaje ya quedó y la entrega no debe reintentarse por esto.</summary>
    private async Task MarkOptOutIfButtonAsync(long deliveryId, MessagingRoute route, InboundMessage message, CancellationToken cancellationToken)
    {
        if (message.QuickReply is not { } button || !optOutButtons.Matches(button))
        {
            return;
        }

        try
        {
            var owners = await dbContext.Database.SqlQuery<Guid>(
                $"""SELECT customer_id AS "Value" FROM messaging.conversations WHERE connection_id = {route.ConnectionId} AND user_id = {message.UserId} AND customer_id IS NOT NULL""")
                .ToListAsync(cancellationToken);
            if (owners is [var customerId])
            {
                await customers.MarkMarketingOptOutByButtonAsync(route.TenantId, customerId, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogButtonOptOutFailed(logger, deliveryId, exception);
        }
    }

    /// <summary>Spec 2026-10-10 §8.3: por phone_number_id si viene; si no, por la WABA (el BSUID es por portafolio y
    /// aplica a todas sus conexiones). P7: también con Paused o el módulo apagado.</summary>
    private async Task ProcessUserIdUpdateAsync(long deliveryId, UserIdUpdateChange update, CancellationToken cancellationToken)
    {
        IReadOnlyList<MessagingRoute> routes = update.PhoneNumberId is { } phoneNumberId
            ? (await routing.FindRouteAsync(phoneNumberId, cancellationToken)) is { } route ? [route] : []
            : await directory.FindByAccountAsync(update.WabaId, cancellationToken);
        if (routes.Count == 0)
        {
            LogIgnoredField(logger, deliveryId, "user_id_update");
            return;
        }

        await ApplyNumberChangeAsync(routes, update.Change, clock.UtcNow, cancellationToken);
    }

    /// <summary>El evento tiene que quedar antes, en el hilo, que los entrantes del mismo change: la hora de proceso los
    /// dejaría después (Meta los fecha antes de entregarlos). Va 4 ms antes del más temprano, por debajo de los eventos
    /// de la ingesta (−1 a −3 ms, <see cref="InboundIngestion"/>), para que nunca compartan milisegundo.</summary>
    private DateTimeOffset NumberChangeOccurredAt(IReadOnlyList<InboundMessage> messages)
    {
        var now = clock.UtcNow;
        if (messages.Count == 0)
        {
            return now;
        }

        var beforeFirst = messages.Min(message => message.OccurredAt).AddMilliseconds(-4);
        return beforeFirst < now ? beforeFirst : now;
    }

    private async Task ApplyNumberChangeAsync(IReadOnlyList<MessagingRoute> routes, UserIdChange change, DateTimeOffset occurredAt, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        foreach (var route in routes)
        {
            await ContactNumberChange.ApplyAsync(dbContext, route.TenantId, route.ConnectionId, change, occurredAt, now, cancellationToken);
        }

        foreach (var tenantId in routes.Select(route => route.TenantId).Distinct())
        {
            await customers.ReplaceUserIdAsync(tenantId, change.Previous, change.Current, cancellationToken);
        }
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
            var applied = await StatusIngestion.ApplyAsync(dbContext, route.ConnectionId, status, cancellationToken);
            var result = applied.Outcome;
            if (result == StatusOutcome.NotFound && status.CallbackMessageId is not null)
            {
                LogStatusPending(logger, deliveryId, attempts);
                outcome = DeliveryOutcome.RetryLater;
            }
            else if (result == StatusOutcome.NotFound)
            {
                LogForeignStatus(logger, deliveryId);
            }
            else if (await StatusIngestion.FindMarketingOptOutCustomerAsync(dbContext, route.ConnectionId, status, cancellationToken) is { } customerId)
            {
                // Spec 2026-10-11 decisión 10: la persona apagó el marketing. Idempotente; si falla, la entrega se reintenta.
                await customers.MarkMarketingOptOutAsync(route.TenantId, customerId, cancellationToken);
            }

            // Spec 2026-10-14: el acuse cambió la foto del hilo (entregado, leído, fallido); quien lo mira lo relee.
            foreach (var conversationId in applied.ConversationIds)
            {
                await NotifyAsync(deliveryId, conversationId, () => notifier.ConversationChangedAsync(route.TenantId, conversationId, cancellationToken));
            }
        }

        return outcome;
    }

    /// <summary>Un fallo del aviso no reintenta la entrega ni deshace nada: lo que se iba a avisar ya quedó commiteado, y el
    /// polling relajado del frontend lo recoge igual.</summary>
    private async Task NotifyAsync(long deliveryId, Guid conversationId, Func<Task> notify)
    {
        try
        {
            await notify();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogNotifyFailed(logger, deliveryId, conversationId, exception);
        }
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

    /// <summary>Spec 2026-10-14 §4.3: por cada conexión de la WABA, la fila del espejo con ese <c>meta_template_id</c> toma el estado y el
    /// motivo. Sin fila sólo hay log por conexión (el refresco la trae). Un <c>event</c> desconocido se guarda como texto; <c>REINSTATED</c> y
    /// <c>FLAGGED</c> pasan por <see cref="MessageTemplateStatuses.FromWebhookEvent"/> (revisión final F2). No se audita (D-T14).</summary>
    private async Task ProcessTemplateStatusAsync(long deliveryId, TemplateStatusChange change, CancellationToken cancellationToken)
    {
        var updated = false;
        var now = clock.UtcNow;
        // Directo al directorio, sin la caché de WebhookRouting: la lista de conexiones tiene que ser la de ahora.
        foreach (var route in await directory.FindByAccountAsync(change.WabaId, cancellationToken))
        {
            var template = await dbContext.MessageTemplates.SingleOrDefaultAsync(
                row => row.TenantId == route.TenantId && row.ConnectionId == route.ConnectionId && row.MetaTemplateId == change.MetaTemplateId,
                cancellationToken);
            if (template is null)
            {
                LogTemplateWithoutRow(logger, deliveryId, change.MetaTemplateId, route.TenantId, route.ConnectionId);
                continue;
            }

            // Revisión final F2: REINSTATED vuelve a APPROVED y FLAGGED no toca la fila (sigue enviable); sólo queda el log.
            if (MessageTemplateStatuses.FromWebhookEvent(change.Event) is not { } status)
            {
                LogTemplateFlagged(logger, deliveryId, change.MetaTemplateId, route.TenantId, route.ConnectionId);
                continue;
            }

            template.ApplyStatus(status, change.Reason, now);
            updated = true;
            LogTemplateStatus(logger, deliveryId, change.MetaTemplateId, change.Event, change.Reason, route.TenantId, route.ConnectionId);
        }

        if (updated)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }
}
