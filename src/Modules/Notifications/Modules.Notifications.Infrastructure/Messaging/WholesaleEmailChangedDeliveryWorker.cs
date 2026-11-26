using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Notifications.Application;
using Modules.Notifications.Domain;
using Modules.Notifications.Infrastructure.Persistence;
using Modules.Tenancy.Domain;

namespace Modules.Notifications.Infrastructure.Messaging;

// Spec 2026-11-19 §6: consume del outbox de plataforma el cambio de correo de un distribuidor al aceptar la invitación de un
// mayorista y avisa a las dos direcciones. Calca WholesaleInvitationDeliveryWorker: payload leído por nombre con JsonDocument y
// nombre del evento literal (este módulo no referencia Mayorista, D-18), destinatario en el payload y nombre del mayorista leído
// al entregar. Son dos consumidores del mismo evento, uno por dirección, cada uno con su inbox: un fallo al mandar a una no
// impide ni repite el envío a la otra, y la aceptación ya quedó guardada. Ni los correos ni el payload se loguean. El reclamo,
// el lote y la cancelación son de OutboxDeliveryWorker.
internal abstract class WholesaleEmailChangedDeliveryWorker(IServiceScopeFactory scopeFactory, ILogger logger)
    : OutboxDeliveryWorker(scopeFactory, logger)
{
    internal const string Event = "wholesale.distributor-email-changed.v1";

    internal const string RecipientUnavailableReason = "recipient_email_unavailable";

    internal const string TenantNameUnavailableReason = "tenant_name_unavailable";

    protected override string EventName => Event;

    protected override string TemplateRef => WholesaleEmailChangedEmailTemplate.TemplateRef;

    /// <summary>A cuál de las dos direcciones manda este consumidor.</summary>
    protected abstract bool ToPreviousAddress { get; }

    protected override Task<Notification> DeliverAsync(
        OutboxRecord record, DeliveryContext context, CancellationToken stoppingToken) =>
        DeliverEmailAsync(record, context, stoppingToken);

    // Interno para las pruebas (InternalsVisibleTo): el armado y el envío sin la base del reclamo.
    internal async Task<Notification> DeliverEmailAsync(
        OutboxRecord record, DeliveryContext context, CancellationToken stoppingToken)
    {
        var payload = Parse(record.PayloadJson);
        var now = context.Clock.UtcNow;
        var address = ToPreviousAddress ? payload.PreviousEmail : payload.NewEmail;
        var notification = Notification.CreateEmail(
            payload.TenantId, payload.DistributorId, address ?? string.Empty, TemplateRef, now);

        // Un dato que falta deja la notificación en Failed con una razón fija y no lanza: lanzar quemaría los reintentos de un
        // mensaje que no se arregla solo.
        if (string.IsNullOrWhiteSpace(address)
            || string.IsNullOrWhiteSpace(payload.PreviousEmail)
            || string.IsNullOrWhiteSpace(payload.NewEmail))
        {
            notification.MarkFailed(RecipientUnavailableReason, now);
            return notification;
        }

        var wholesalerName = await context.TenantDirectory.GetDisplayNameAsync(new TenantId(payload.TenantId), stoppingToken);
        if (string.IsNullOrWhiteSpace(wholesalerName))
        {
            notification.MarkFailed(TenantNameUnavailableReason, now);
            return notification;
        }

        string recipient = address;
        string previous = payload.PreviousEmail;
        string next = payload.NewEmail;
        string wholesaler = wholesalerName;
        var toPrevious = ToPreviousAddress;
        await SendAsync(
            context,
            notification,
            () => WholesaleEmailChangedEmailTemplate.Render(
                recipient, toPrevious, wholesaler, payload.BusinessName, previous, next),
            stoppingToken);
        return notification;
    }

    private static EmailChangedPayload Parse(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        var root = document.RootElement;
        return new EmailChangedPayload(
            root.GetProperty("tenantId").GetGuid(),
            root.GetProperty("distributorId").GetGuid(),
            ReadString(root, "previousEmail"),
            ReadString(root, "newEmail"),
            ReadString(root, "businessName") ?? string.Empty);
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String ? element.GetString() : null;

    private sealed record EmailChangedPayload(
        Guid TenantId,
        Guid DistributorId,
        string? PreviousEmail,
        string? NewEmail,
        string BusinessName);
}

/// <summary>El aviso al correo que había escrito el mayorista.</summary>
internal sealed class WholesaleEmailChangedToPreviousDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<WholesaleEmailChangedToPreviousDeliveryWorker> logger)
    : WholesaleEmailChangedDeliveryWorker(scopeFactory, logger)
{
    protected override string Consumer => "notifications.wholesale-email-changed-previous";

    protected override bool ToPreviousAddress => true;
}

/// <summary>El aviso al correo de la cuenta que aceptó.</summary>
internal sealed class WholesaleEmailChangedToNewDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<WholesaleEmailChangedToNewDeliveryWorker> logger)
    : WholesaleEmailChangedDeliveryWorker(scopeFactory, logger)
{
    protected override string Consumer => "notifications.wholesale-email-changed-new";

    protected override bool ToPreviousAddress => false;
}
