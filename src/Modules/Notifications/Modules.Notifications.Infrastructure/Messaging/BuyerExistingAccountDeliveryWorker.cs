using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Buyers.Application;
using Modules.Notifications.Application;
using Modules.Notifications.Domain;
using Modules.Notifications.Infrastructure.Persistence;

namespace Modules.Notifications.Infrastructure.Messaging;

// Consume el aviso de cuenta existente del comprador. No lleva token.
// El destinatario sale del payload, no de IUserDirectory: la cuenta de comprador no es un usuario de
// Identity. El token y el correo no se loguean ni entran en ningún mensaje de excepción.
internal sealed class BuyerExistingAccountDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<BuyerExistingAccountDeliveryWorker> logger)
    : OutboxDeliveryWorker(scopeFactory, logger)
{
    protected override string Consumer => "notifications.buyer-existing-account-email";

    protected override string EventName => BuyerIntegrationEvents.ExistingAccountNotice;

    protected override string TemplateRef => BuyerExistingAccountEmailTemplate.TemplateRef;

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

        await SendAsync(
            context,
            notification,
            () => BuyerExistingAccountEmailTemplate.Render(
                payload.Email,
                payload.StoreName,
                BuyerAccountLinks.Login(payload.StoreHost),
                BuyerAccountLinks.Forgot(payload.StoreHost)),
            stoppingToken);
        return notification;
    }
}
