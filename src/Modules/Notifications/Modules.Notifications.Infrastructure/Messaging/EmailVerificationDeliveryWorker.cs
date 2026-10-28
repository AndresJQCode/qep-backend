using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Identity.Application;
using Modules.Notifications.Application;
using Modules.Notifications.Domain;
using Modules.Notifications.Infrastructure.Persistence;

namespace Modules.Notifications.Infrastructure.Messaging;

// Consume del outbox de plataforma el pedido de verificación de correo y entrega el enlace. El token
// crudo viaja sólo dentro del enlace: no se loguea ni entra en el mensaje de ninguna excepción.
internal sealed class EmailVerificationDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<NotificationsOptions> options,
    ILogger<EmailVerificationDeliveryWorker> logger)
    : OutboxDeliveryWorker(scopeFactory, logger)
{
    protected override string Consumer => "notifications.email-verification-email";

    protected override string EventName => IdentityIntegrationEvents.EmailVerificationRequested;

    protected override string TemplateRef => EmailVerificationEmailTemplate.TemplateRef;

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
            () => EmailVerificationEmailTemplate.Render(
                recipient, AccountLinks.VerifyEmail(options.Value.AppBaseUrl, rawToken)),
            stoppingToken);
        return notification;
    }
}

/// <summary>Lee el payload de los eventos de cuenta. Los mensajes de error de System.Text.Json no
/// incluyen valores del documento, así que el token no se filtra por acá.</summary>
internal static class AccountEmailPayload
{
    public static (Guid UserId, string? Token) Parse(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        var root = document.RootElement;
        var userId = root.GetProperty("userId").GetGuid();
        var token = root.TryGetProperty("token", out var tokenElement) && tokenElement.ValueKind == JsonValueKind.String
            ? tokenElement.GetString()
            : null;
        return (userId, token);
    }
}
