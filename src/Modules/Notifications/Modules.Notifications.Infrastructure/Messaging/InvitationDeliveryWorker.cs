using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Notifications.Application;
using Modules.Notifications.Domain;
using Modules.Notifications.Infrastructure.Persistence;

namespace Modules.Notifications.Infrastructure.Messaging;

// Consume del outbox de plataforma el evento de membresía invitada y entrega el email de invitación.
// El reclamo, el lote y la cancelación son de OutboxDeliveryWorker.
internal sealed class InvitationDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<NotificationsOptions> options,
    ILogger<InvitationDeliveryWorker> logger)
    : OutboxDeliveryWorker(scopeFactory, logger)
{
    protected override string Consumer => "notifications.invitation-email";

    protected override string EventName => "tenancy.membership-invited.v1";

    protected override string TemplateRef => InvitationEmailTemplate.TemplateRef;

    protected override async Task<Notification> DeliverAsync(
        OutboxRecord record, DeliveryContext context, CancellationToken stoppingToken)
    {
        var (userId, tenantId, token) = ParsePayload(record.PayloadJson);
        var (notification, email) = await ResolveRecipientAsync(context, tenantId, userId, TemplateRef, stoppingToken);
        if (email is null)
        {
            return notification;
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            // Un evento anterior al token de invitación (encolado antes del despliegue) no tiene link
            // que armar. Se marca fallido en vez de tirar: una excepción lo reintentaría hasta cortarlo
            // por intentos, y el resultado sería el mismo tres ticks más tarde.
            notification.MarkFailed("invitation_token_unavailable", context.Clock.UtcNow);
            return notification;
        }

        string recipient = email;
        string invitationToken = token;
        await SendAsync(
            context,
            notification,
            () => InvitationEmailTemplate.Render(
                recipient, InvitationLink.Compose(options.Value.InvitationUrl, invitationToken)),
            stoppingToken);
        return notification;
    }

    private static (Guid UserId, Guid TenantId, string? Token) ParsePayload(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        var root = document.RootElement;
        var userId = root.GetProperty("userId").GetGuid();
        var tenantId = root.GetProperty("tenantId").GetProperty("value").GetGuid();
        // TryGetProperty y no GetProperty: los mensajes encolados antes del despliegue del token no lo
        // traen, y esos se resuelven como fallo marcado, no como mensaje envenenado.
        var token = root.TryGetProperty("token", out var tokenElement)
            ? tokenElement.GetString()
            : null;
        return (userId, tenantId, token);
    }
}
