using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Infrastructure.Webhook;

/// <summary>
/// Spec 2026-10-14 §8.2: la mitad de Instagram del procesador. Recorre <c>entry[]</c>; cada evento es idempotente (el <c>mid</c> es la
/// clave del mensaje, el visto y el borrado son monótonos), así que reprocesar una entrega a medias no duplica nada. La ruta es la
/// cuenta profesional (<c>entry[].id</c>), con la misma caché que el <c>phone_number_id</c>. Nunca registra el contenido ni el token.
/// </summary>
internal sealed partial class WebhookDeliveryProcessor
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook delivery {DeliveryId}: {Count} Instagram event(s) discarded for account {AccountId} ({Reason}).")]
    private static partial void LogInstagramDiscarded(ILogger logger, long deliveryId, int count, string accountId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook delivery {DeliveryId}: {Count} Instagram event(s) ignored ({Reasons}).")]
    private static partial void LogInstagramIgnored(ILogger logger, long deliveryId, int count, string reasons);

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook delivery {DeliveryId}: an Instagram seen or deletion matched no message and was discarded.")]
    private static partial void LogInstagramForeign(ILogger logger, long deliveryId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook delivery {DeliveryId}: the Instagram profile of the sender could not be read ({Reason}); the conversation opens without a name.")]
    private static partial void LogInstagramProfileFailed(ILogger logger, long deliveryId, string reason);

    private async Task<DeliveryOutcome> ProcessInstagramAsync(long deliveryId, string payload, CancellationToken cancellationToken)
    {
        foreach (var entry in InstagramWebhookPayloadParser.Parse(payload))
        {
            var ignored = entry.Events.OfType<InstagramIgnoredEvent>().ToArray();
            if (ignored.Length > 0)
            {
                var reasons = string.Join(",", ignored.Select(e => e.Reason).Distinct());
                LogInstagramIgnored(logger, deliveryId, ignored.Length, reasons);
            }

            var route = await routing.FindInstagramRouteAsync(entry.AccountId, cancellationToken);
            if (route is null)
            {
                LogInstagramDiscarded(logger, deliveryId, entry.Events.Count, entry.AccountId, "unknown-route");
                continue;
            }

            // D-M18, igual que WhatsApp: con Paused o el módulo apagado los mensajes se descartan; el visto y el borrado se aplican igual,
            // que son estado de lo que ya está.
            var accepting = !route.IsPaused && await routing.IsModuleEnabledAsync(route.TenantId, cancellationToken);
            var messages = entry.Events.OfType<InstagramMessageEvent>().ToArray();
            if (!accepting && messages.Length > 0)
            {
                LogInstagramDiscarded(logger, deliveryId, messages.Length, entry.AccountId, route.IsPaused ? "paused" : "module-off");
            }

            InstagramSender? sender = null;
            var senderResolved = false;
            foreach (var @event in entry.Events)
            {
                switch (@event)
                {
                    case InstagramMessageEvent message when accepting:
                        if (!senderResolved)
                        {
                            sender = await directory.ResolveInstagramSenderAsync(route.TenantId, route.ConnectionId, cancellationToken);
                            senderResolved = true;
                        }

                        await IngestInstagramAsync(deliveryId, route, sender, message, cancellationToken);
                        break;
                    case InstagramSeenEvent seen:
                        // Spec 2026-10-14 (tiempo real): como un acuse de WhatsApp, el visto cambió la foto del hilo y quien lo mira lo relee.
                        var applied = await StatusIngestion.ApplySeenAsync(dbContext, route.ConnectionId, seen.UserId, seen.Mid, cancellationToken);
                        if (applied.Outcome == StatusOutcome.NotFound)
                        {
                            LogInstagramForeign(logger, deliveryId);
                        }

                        foreach (var conversationId in applied.ConversationIds)
                        {
                            await NotifyAsync(deliveryId, conversationId, () => notifier.ConversationChangedAsync(route.TenantId, conversationId, cancellationToken));
                        }

                        break;
                    case InstagramDeletedEvent deleted:
                        if (await StatusIngestion.MarkDeletedAsync(dbContext, route.ConnectionId, deleted.UserId, deleted.Mid, cancellationToken) is { } deletedIn)
                        {
                            await NotifyAsync(deliveryId, deletedIn, () => notifier.ConversationChangedAsync(route.TenantId, deletedIn, cancellationToken));
                        }
                        else
                        {
                            LogInstagramForeign(logger, deliveryId);
                        }

                        break;
                    default:
                        break;
                }
            }
        }

        return DeliveryOutcome.Processed;
    }

    /// <summary>Spec 2026-10-14 §8.2: con conversación y cliente no se llama a nadie. Si la conversación es nueva se pide el perfil
    /// (nombre y usuario) con el token de la cuenta, best effort, y se asegura el cliente por <c>instagram_user_id</c> (decisión 10).</summary>
    private async Task IngestInstagramAsync(long deliveryId, MessagingRoute route, InstagramSender? sender, InstagramMessageEvent @event, CancellationToken cancellationToken)
    {
        var message = @event.Message;
        var existing = await dbContext.Database.SqlQuery<ExistingConversation>(
            $"""SELECT id AS "Id", customer_id AS "CustomerId" FROM messaging.conversations WHERE connection_id = {route.ConnectionId} AND user_id = {message.UserId}""")
            .ToListAsync(cancellationToken);

        var context = InboundContext.None;
        if (existing is not [{ CustomerId: not null }])
        {
            if (existing.Count == 0 && sender is not null)
            {
                var profile = await instagram.GetProfileAsync(sender, message.UserId, cancellationToken);
                if (profile.Succeeded && profile.Value is { } found)
                {
                    message = message with
                    {
                        ProfileName = Truncate(found.Name, Conversation.ProfileNameMaxLength),
                        Username = Truncate(found.Username, Conversation.UsernameMaxLength),
                    };
                }
                else
                {
                    LogInstagramProfileFailed(logger, deliveryId, profile.Failure?.Reason ?? "empty");
                }
            }

            var customer = await customers.EnsureAsync(
                route.TenantId, new MessagingContact(message.UserId, null, message.ProfileName, message.Username, ConversationChannel.Instagram), cancellationToken);
            Guid? heir = null;
            if (existing.Count == 0)
            {
                var candidates = await dbContext.Database.SqlQuery<Guid>(
                    $"""
                    SELECT assigned_member_id AS "Value" FROM messaging.conversations
                    WHERE tenant_id = {route.TenantId} AND customer_id = {customer.CustomerId} AND assigned_member_id IS NOT NULL
                    ORDER BY last_activity_at DESC
                    LIMIT 1
                    """).ToListAsync(cancellationToken);
                if (candidates is [var candidate] && await assignees.CanReplyAsync(route.TenantId, candidate, cancellationToken))
                {
                    heir = candidate;
                }
            }

            context = new InboundContext(customer.CustomerId, customer.Created, heir);
        }

        // Spec 2026-10-14 (tiempo real): ya commiteado; un reenvío de Meta (null) no avisa nada. Un DM suena como uno de WhatsApp;
        // un echo movió la foto de la lista pero no es un entrante, así que sólo avisa que la conversación cambió.
        var now = clock.UtcNow;
        if (@event.IsEcho)
        {
            var echoed = await InboundIngestion.IngestEchoAsync(dbContext, route.TenantId, route.ConnectionId, message, context, now, ConversationChannel.Instagram, cancellationToken);
            if (echoed is not null)
            {
                await NotifyAsync(deliveryId, echoed.ConversationId, () => notifier.ConversationChangedAsync(route.TenantId, echoed.ConversationId, cancellationToken));
            }
        }
        else
        {
            var ingested = await InboundIngestion.IngestAsync(dbContext, route.TenantId, route.ConnectionId, message, context, now, cancellationToken, ConversationChannel.Instagram);
            if (ingested is not null)
            {
                await NotifyAsync(deliveryId, ingested.ConversationId,
                    () => notifier.InboundMessageAsync(route.TenantId, ingested.ConversationId, ingested.MessageId, ingested.AssignedMemberId, cancellationToken));
            }
        }
    }

    private static string? Truncate(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= maxLength ? value : value[..maxLength];
}
