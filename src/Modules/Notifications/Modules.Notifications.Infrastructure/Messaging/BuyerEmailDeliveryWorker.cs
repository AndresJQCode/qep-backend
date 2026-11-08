using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Buyers.Application;
using Modules.Notifications.Application;
using Modules.Notifications.Domain;
using Modules.Notifications.Infrastructure.Persistence;

namespace Modules.Notifications.Infrastructure.Messaging;

/// <summary>
/// Lo que comparten los workers de los correos del comprador: leer el payload, armar la notificación
/// sin tenant con el destinatario del payload (la cuenta de comprador no es un usuario de Identity) y los
/// chequeos previos al envío. Un dato que falta deja la notificación en Failed con una razón fija y no
/// lanza: lanzar quemaría los reintentos de un mensaje que no se arregla solo. Ni el correo ni el token
/// ni el enlace se loguean.
/// </summary>
internal abstract class BuyerEmailDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    ILogger logger)
    : OutboxDeliveryWorker(scopeFactory, logger)
{
    internal const string StoreHostInvalidReason = "store_host_invalid";

    /// <summary>Si el correo lleva un token (verificar, restablecer).</summary>
    protected virtual bool RequiresToken => false;

    /// <summary>Si el correo lleva enlaces al host de la tienda.</summary>
    protected virtual bool LinksToStore => true;

    protected abstract EmailMessage Render(BuyerEmailPayload payload);

    protected override async Task<Notification> DeliverAsync(
        OutboxRecord record, DeliveryContext context, CancellationToken stoppingToken)
    {
        var payload = BuyerEmailPayloadReader.Parse(record.PayloadJson);
        var now = context.Clock.UtcNow;
        var notification = Notification.CreateEmail(
            null, payload.AccountId, payload.Email ?? string.Empty, TemplateRef, now);

        if (string.IsNullOrWhiteSpace(payload.Email))
        {
            notification.MarkFailed("recipient_email_unavailable", now);
            return notification;
        }

        if (LinksToStore && !IsValidStoreHost(payload.StoreHost))
        {
            notification.MarkFailed(StoreHostInvalidReason, now);
            return notification;
        }

        if (RequiresToken && string.IsNullOrWhiteSpace(payload.Token))
        {
            notification.MarkFailed("email_token_unavailable", now);
            return notification;
        }

        await SendAsync(context, notification, () => Render(payload), stoppingToken);
        return notification;
    }

    private static bool IsValidStoreHost(string? host) =>
        !string.IsNullOrWhiteSpace(host) && Uri.CheckHostName(host) == UriHostNameType.Dns;
}
