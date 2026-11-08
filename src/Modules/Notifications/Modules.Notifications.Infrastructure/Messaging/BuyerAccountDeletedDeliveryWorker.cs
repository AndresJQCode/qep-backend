using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Buyers.Application;
using Modules.Notifications.Application;

namespace Modules.Notifications.Infrastructure.Messaging;

// Consume la confirmación de cuenta eliminada del comprador. No lleva token ni enlaces.
internal sealed class BuyerAccountDeletedDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<BuyerAccountDeletedDeliveryWorker> logger)
    : BuyerEmailDeliveryWorker(scopeFactory, logger)
{
    protected override string Consumer => "notifications.buyer-account-deleted-email";

    protected override string EventName => BuyerIntegrationEvents.AccountDeleted;

    protected override string TemplateRef => BuyerAccountDeletedEmailTemplate.TemplateRef;

    protected override bool LinksToStore => false;

    protected override EmailMessage Render(BuyerEmailPayload payload) =>
        BuyerAccountDeletedEmailTemplate.Render(payload.Email, payload.StoreName);
}
