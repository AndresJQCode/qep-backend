using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Identity.Application;
using Modules.Notifications.Application;
using Modules.Notifications.Domain;
using Modules.Notifications.Infrastructure.Persistence;

namespace Modules.Notifications.Infrastructure.Messaging;

// Consume del outbox de plataforma el pedido de restablecer contraseña y entrega el enlace. El token
// crudo viaja sólo dentro del enlace: no se loguea ni entra en el mensaje de ninguna excepción.
internal sealed class PasswordResetDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<NotificationsOptions> options,
    ILogger<PasswordResetDeliveryWorker> logger)
    : OutboxDeliveryWorker(scopeFactory, logger)
{
    protected override string Consumer => "notifications.password-reset-email";

    protected override string EventName => IdentityIntegrationEvents.PasswordResetRequested;

    protected override string TemplateRef => PasswordResetEmailTemplate.TemplateRef;

    protected override async Task<Notification> DeliverAsync(
        OutboxRecord record, DeliveryContext context, CancellationToken stoppingToken)
    {
        var (userId, token) = AccountEmailPayload.Parse(record.PayloadJson);
        var (notification, email) = await ResolveRecipientAsync(context, null, userId, TemplateRef, stoppingToken);
        if (email is null)
        {
            return notification;
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            notification.MarkFailed("email_token_unavailable", context.Clock.UtcNow);
            return notification;
        }

        string recipient = email;
        string rawToken = token;
        await SendAsync(
            context,
            notification,
            () => PasswordResetEmailTemplate.Render(
                recipient, AccountLinks.ResetPassword(options.Value.AppBaseUrl, rawToken)),
            stoppingToken);
        return notification;
    }
}
