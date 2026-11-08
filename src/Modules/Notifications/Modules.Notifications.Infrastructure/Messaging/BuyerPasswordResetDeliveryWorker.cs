using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Buyers.Application;
using Modules.Notifications.Application;

namespace Modules.Notifications.Infrastructure.Messaging;

// Consume el pedido de restablecer la contraseña del comprador.
internal sealed class BuyerPasswordResetDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<BuyerPasswordResetDeliveryWorker> logger)
    : BuyerEmailDeliveryWorker(scopeFactory, logger)
{
    protected override string Consumer => "notifications.buyer-password-reset-email";

    protected override string EventName => BuyerIntegrationEvents.PasswordResetRequested;

    protected override string TemplateRef => BuyerPasswordResetEmailTemplate.TemplateRef;

    protected override bool RequiresToken => true;

    protected override EmailMessage Render(BuyerEmailPayload payload) =>
        BuyerPasswordResetEmailTemplate.Render(
            payload.Email, payload.StoreName, BuyerAccountLinks.Reset(payload.StoreHost, payload.Token!));
}
