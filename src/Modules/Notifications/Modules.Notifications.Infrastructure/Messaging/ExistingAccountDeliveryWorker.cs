using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Identity.Application;
using Modules.Notifications.Application;
using Modules.Notifications.Domain;
using Modules.Notifications.Infrastructure.Persistence;

namespace Modules.Notifications.Infrastructure.Messaging;

// Consume del outbox de plataforma el aviso de cuenta existente. No lleva token: sólo enlaces a login y
// a recuperar la contraseña.
internal sealed class ExistingAccountDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<NotificationsOptions> options,
    ILogger<ExistingAccountDeliveryWorker> logger)
    : OutboxDeliveryWorker(scopeFactory, logger)
{
    protected override string Consumer => "notifications.existing-account-email";

    protected override string EventName => IdentityIntegrationEvents.ExistingAccountNotice;

    protected override string TemplateRef => ExistingAccountEmailTemplate.TemplateRef;

    protected override async Task<Notification> DeliverAsync(
        OutboxRecord record, DeliveryContext context, CancellationToken stoppingToken)
    {
        var (userId, _) = AccountEmailPayload.Parse(record.PayloadJson);
        var (notification, email) = await ResolveRecipientAsync(context, null, userId, TemplateRef, stoppingToken);
        if (email is null)
        {
            return notification;
        }

        string recipient = email;
        var baseUrl = options.Value.AppBaseUrl;
        await SendAsync(
            context,
            notification,
            () => ExistingAccountEmailTemplate.Render(
                recipient, AccountLinks.Login(baseUrl), AccountLinks.ForgotPassword(baseUrl)),
            stoppingToken);
        return notification;
    }
}
