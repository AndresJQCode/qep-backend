using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Buyers.Application;
using Modules.Notifications.Application;

namespace Modules.Notifications.Infrastructure.Messaging;

// Consume el pedido de verificación del comprador y entrega el enlace.
internal sealed class BuyerVerificationDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<BuyerVerificationDeliveryWorker> logger)
    : BuyerEmailDeliveryWorker(scopeFactory, logger)
{
    protected override string Consumer => "notifications.buyer-verification-email";

    protected override string EventName => BuyerIntegrationEvents.EmailVerificationRequested;

    protected override string TemplateRef => BuyerVerificationEmailTemplate.TemplateRef;

    protected override bool RequiresToken => true;

    protected override EmailMessage Render(BuyerEmailPayload payload) =>
        BuyerVerificationEmailTemplate.Render(
            payload.Email, payload.StoreName, BuyerAccountLinks.Verify(payload.StoreHost, payload.Token!));
}
