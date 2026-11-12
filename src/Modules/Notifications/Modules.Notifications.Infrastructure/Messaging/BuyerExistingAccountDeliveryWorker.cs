using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Buyers.Application;
using Modules.Notifications.Application;

namespace Modules.Notifications.Infrastructure.Messaging;

// Consume el aviso de cuenta existente del comprador. No lleva token.
internal sealed class BuyerExistingAccountDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<BuyerExistingAccountDeliveryWorker> logger)
    : BuyerEmailDeliveryWorker(scopeFactory, logger)
{
    protected override string Consumer => "notifications.buyer-existing-account-email";

    protected override string EventName => BuyerIntegrationEvents.ExistingAccountNotice;

    protected override string TemplateRef => BuyerExistingAccountEmailTemplate.TemplateRef;

    protected override EmailMessage Render(BuyerEmailPayload payload) =>
        BuyerExistingAccountEmailTemplate.Render(
            payload.Email,
            payload.StoreName,
            BuyerAccountLinks.Login(payload.StoreHost),
            BuyerAccountLinks.Forgot(payload.StoreHost));
}
