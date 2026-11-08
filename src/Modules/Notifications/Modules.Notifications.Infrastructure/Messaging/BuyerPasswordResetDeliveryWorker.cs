using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Buyers.Application;
using Modules.Notifications.Application;
using Modules.Notifications.Domain;
using Modules.Notifications.Infrastructure.Persistence;

namespace Modules.Notifications.Infrastructure.Messaging;

// Consume el pedido de restablecer la contraseña del comprador.
// El destinatario sale del payload, no de IUserDirectory: la cuenta de comprador no es un usuario de
// Identity. El token y el correo no se loguean ni entran en ningún mensaje de excepción.
internal sealed class BuyerPasswordResetDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<BuyerPasswordResetDeliveryWorker> logger)
    : OutboxDeliveryWorker(scopeFactory, logger)
{
    protected override string Consumer => "notifications.buyer-password-reset-email";

    protected override string EventName => BuyerIntegrationEvents.PasswordResetRequested;

    protected override string TemplateRef => BuyerPasswordResetEmailTemplate.TemplateRef;

    protected override async Task<Notification> DeliverAsync(
        OutboxRecord record, DeliveryContext context, CancellationToken stoppingToken)
    {
        var payload = BuyerEmailPayloadReader.Parse(record.PayloadJson);
        var notification = Notification.CreateEmail(
            null, payload.AccountId, payload.Email ?? string.Empty, TemplateRef, context.Clock.UtcNow);
        if (string.IsNullOrWhiteSpace(payload.Email))
        {
            notification.MarkFailed("recipient_email_unavailable", context.Clock.UtcNow);
            return notification;
        }

        if (string.IsNullOrWhiteSpace(payload.Token))
        {
            notification.MarkFailed("email_token_unavailable", context.Clock.UtcNow);
            return notification;
        }

        await SendAsync(
            context,
            notification,
            () => BuyerPasswordResetEmailTemplate.Render(
                payload.Email, payload.StoreName, BuyerAccountLinks.Reset(payload.StoreHost, payload.Token)),
            stoppingToken);
        return notification;
    }
}
