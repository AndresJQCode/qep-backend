using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Notifications.Application;
using Modules.Notifications.Domain;
using Modules.Notifications.Infrastructure.Persistence;

namespace Modules.Notifications.Infrastructure.Messaging;

// Consume `quotations.export-failed.v1`: a quien pidió la exportación le avisa que no le va a llegar el
// archivo y que puede pedirlo de nuevo. El reclamo, el lote y la cancelación son de OutboxDeliveryWorker.
internal sealed class QuotationsExportFailedDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<QuotationsExportFailedDeliveryWorker> logger)
    : OutboxDeliveryWorker(scopeFactory, logger)
{
    protected override string Consumer => "notifications.quotations-export-failed-email";

    protected override string EventName => "quotations.export-failed.v1";

    protected override string TemplateRef => QuotationsExportFailedEmailTemplate.TemplateRef;

    protected override async Task<Notification> DeliverAsync(
        OutboxRecord record, DeliveryContext context, CancellationToken stoppingToken)
    {
        var export = ParsePayload(record.PayloadJson);
        var (notification, email) = await ResolveRecipientAsync(
            context, export.TenantId, export.SubjectId, TemplateRef, stoppingToken);
        if (email is null)
        {
            return notification;
        }

        string recipient = email;
        await SendAsync(
            context,
            notification,
            () => QuotationsExportFailedEmailTemplate.Render(recipient, export.Kind),
            stoppingToken);
        return notification;
    }

    private static FailedPayload ParsePayload(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        var root = document.RootElement;
        return new FailedPayload(
            root.GetProperty("tenantId").GetGuid(),
            root.GetProperty("subjectId").GetGuid(),
            root.GetProperty("kind").GetString() ?? string.Empty);
    }

    private sealed record FailedPayload(Guid TenantId, Guid SubjectId, string Kind);
}
