using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Identity.Application;
using Modules.Notifications.Application;
using Modules.Notifications.Domain;
using Modules.Notifications.Infrastructure.Persistence;

namespace Modules.Notifications.Infrastructure.Messaging;

// Spec 2026-11-19 (onboarding por link): consume del outbox de plataforma el código que confirma un correo y lo entrega. Mismo
// patrón que EmailVerificationDeliveryWorker, pero el destinatario viaja en el payload porque todavía no hay usuario
// (precedente WholesaleInvitationDeliveryWorker). Ni el correo ni el código se loguean ni entran en el mensaje de una
// excepción. El reclamo, el lote y la cancelación son de OutboxDeliveryWorker.
internal sealed class EmailChallengeDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<EmailChallengeDeliveryWorker> logger)
    : OutboxDeliveryWorker(scopeFactory, logger)
{
    internal const string RecipientUnavailableReason = "recipient_email_unavailable";

    internal const string CodeUnavailableReason = "email_code_unavailable";

    protected override string Consumer => "notifications.email-challenge-email";

    protected override string EventName => IdentityIntegrationEvents.EmailChallengeRequested;

    protected override string TemplateRef => EmailChallengeEmailTemplate.TemplateRef;

    protected override async Task<Notification> DeliverAsync(
        OutboxRecord record, DeliveryContext context, CancellationToken stoppingToken)
    {
        var payload = EmailChallengePayload.Parse(record.PayloadJson);
        var now = context.Clock.UtcNow;
        var notification = Notification.CreateEmail(
            null, payload.ChallengeId, payload.Email ?? string.Empty, TemplateRef, now);

        // Un dato que falta deja la notificación en Failed con una razón fija y no lanza: lanzar quemaría los reintentos de
        // un mensaje que no se arregla solo.
        if (string.IsNullOrWhiteSpace(payload.Email))
        {
            notification.MarkFailed(RecipientUnavailableReason, now);
            return notification;
        }

        if (string.IsNullOrWhiteSpace(payload.Code))
        {
            notification.MarkFailed(CodeUnavailableReason, now);
            return notification;
        }

        string recipient = payload.Email;
        string code = payload.Code;
        await SendAsync(
            context,
            notification,
            () => EmailChallengeEmailTemplate.Render(recipient, code, EmailChallengeService.Lifetime),
            stoppingToken);
        return notification;
    }
}

/// <summary>Lee el payload de <see cref="IdentityIntegrationEvents.EmailChallengeRequested"/> por nombre. Los mensajes de error
/// de System.Text.Json no incluyen valores del documento, así que el código no se filtra por acá.</summary>
internal static class EmailChallengePayload
{
    public static Parsed Parse(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        var root = document.RootElement;
        return new Parsed(
            root.GetProperty("challengeId").GetGuid(),
            ReadString(root, "email"),
            ReadString(root, "code"));
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    internal sealed record Parsed(Guid ChallengeId, string? Email, string? Code);
}
