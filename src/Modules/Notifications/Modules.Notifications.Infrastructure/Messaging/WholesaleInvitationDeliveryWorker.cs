using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Notifications.Application;
using Modules.Notifications.Domain;
using Modules.Notifications.Infrastructure.Persistence;
using Modules.Tenancy.Domain;

namespace Modules.Notifications.Infrastructure.Messaging;

// Consume del outbox de plataforma la invitación de un mayorista a un distribuidor y entrega el correo con el link. Lee el payload
// por nombre con JsonDocument y el nombre del evento literal: este módulo no referencia Mayorista (D-18, precedente
// SupplierExportDeliveryWorker). El destinatario viaja en el payload porque todavía no es un usuario de Identity (precedente
// BuyerEmailDeliveryWorker). El link sale de AppBaseUrl (ruling W1) y lleva como ?ev= la prueba de buzón del payload, si viene
// (spec 2026-11-24; los eventos anteriores no la traen). Ni el correo, ni el token, ni la prueba, ni el link se loguean. El
// reclamo, el lote y la cancelación son de OutboxDeliveryWorker.
internal sealed class WholesaleInvitationDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<NotificationsOptions> options,
    ILogger<WholesaleInvitationDeliveryWorker> logger)
    : OutboxDeliveryWorker(scopeFactory, logger)
{
    internal const string RecipientUnavailableReason = "recipient_email_unavailable";

    internal const string TokenUnavailableReason = "invitation_token_unavailable";

    internal const string TenantNameUnavailableReason = "tenant_name_unavailable";

    protected override string Consumer => "notifications.wholesale-invitation-email";

    protected override string EventName => "wholesale.distributor-invited.v1";

    protected override string TemplateRef => WholesaleInvitationEmailTemplate.TemplateRef;

    protected override async Task<Notification> DeliverAsync(
        OutboxRecord record, DeliveryContext context, CancellationToken stoppingToken)
    {
        var payload = ParsePayload(record.PayloadJson);
        var now = context.Clock.UtcNow;
        var notification = Notification.CreateEmail(
            payload.TenantId, payload.DistributorId, payload.Email ?? string.Empty, TemplateRef, now);

        // Un dato que falta deja la notificación en Failed con una razón fija y no lanza: lanzar quemaría los reintentos de un
        // mensaje que no se arregla solo.
        if (string.IsNullOrWhiteSpace(payload.Email))
        {
            notification.MarkFailed(RecipientUnavailableReason, now);
            return notification;
        }

        if (string.IsNullOrWhiteSpace(payload.Token))
        {
            notification.MarkFailed(TokenUnavailableReason, now);
            return notification;
        }

        // El nombre del mayorista se lee al entregar, como en InvitationDeliveryWorker: un tenant renombrado entre la invitación
        // y el envío sale con el nombre vigente.
        var wholesalerName = await context.TenantDirectory.GetDisplayNameAsync(new TenantId(payload.TenantId), stoppingToken);
        if (string.IsNullOrWhiteSpace(wholesalerName))
        {
            notification.MarkFailed(TenantNameUnavailableReason, now);
            return notification;
        }

        // El vencimiento en la hora y con el patrón del tenant del mayorista; un tenant que no resuelve falla acá, fuera del
        // envío, y se reintenta.
        var format = await ResolveEmailFormatAsync(context, payload.TenantId, stoppingToken);
        string recipient = payload.Email;
        string link = AccountLinks.WholesaleInvitation(options.Value.AppBaseUrl, payload.Token, payload.EmailProof);
        string wholesaler = wholesalerName;
        await SendAsync(
            context,
            notification,
            () => WholesaleInvitationEmailTemplate.Render(
                recipient, link, wholesaler, payload.BusinessName, payload.ExpiresAt, format),
            stoppingToken);
        return notification;
    }

    private static InvitationPayload ParsePayload(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        var root = document.RootElement;
        return new InvitationPayload(
            root.GetProperty("tenantId").GetGuid(),
            root.GetProperty("distributorId").GetGuid(),
            root.TryGetProperty("email", out var email) ? email.GetString() : null,
            root.TryGetProperty("businessName", out var businessName) ? businessName.GetString() ?? string.Empty : string.Empty,
            root.TryGetProperty("token", out var token) ? token.GetString() : null,
            root.TryGetProperty("emailProof", out var emailProof) ? emailProof.GetString() : null,
            root.GetProperty("expiresAt").GetDateTimeOffset());
    }

    private sealed record InvitationPayload(
        Guid TenantId,
        Guid DistributorId,
        string? Email,
        string BusinessName,
        string? Token,
        string? EmailProof,
        DateTimeOffset ExpiresAt);
}
