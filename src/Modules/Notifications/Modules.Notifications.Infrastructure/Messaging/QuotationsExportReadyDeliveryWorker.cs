using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Notifications.Application;
using Modules.Notifications.Domain;
using Modules.Notifications.Infrastructure.Persistence;

namespace Modules.Notifications.Infrastructure.Messaging;

// Consume `quotations.export-ready.v1` y le manda a quien pidió la exportación el correo con el enlace,
// que ya viene prefirmado: este módulo no conoce Storage. Un worker por evento, igual que clientes y
// productos. El reclamo, el lote y la cancelación son de OutboxDeliveryWorker.
internal sealed class QuotationsExportReadyDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<QuotationsExportReadyDeliveryWorker> logger)
    : OutboxDeliveryWorker(scopeFactory, logger)
{
    protected override string Consumer => "notifications.quotations-export-ready-email";

    protected override string EventName => "quotations.export-ready.v1";

    protected override string TemplateRef => QuotationsExportReadyEmailTemplate.TemplateRef;

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
            () => QuotationsExportReadyEmailTemplate.Render(
                recipient, export.Kind, export.DownloadUrl, export.FileName, export.RowCount, export.ExpiresAt),
            stoppingToken);
        return notification;
    }

    private static ReadyPayload ParsePayload(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        var root = document.RootElement;
        return new ReadyPayload(
            root.GetProperty("tenantId").GetGuid(),
            root.GetProperty("subjectId").GetGuid(),
            root.GetProperty("kind").GetString() ?? string.Empty,
            root.GetProperty("downloadUrl").GetString() ?? string.Empty,
            root.GetProperty("fileName").GetString() ?? string.Empty,
            root.GetProperty("rowCount").GetInt32(),
            root.GetProperty("expiresAt").GetDateTimeOffset());
    }

    private sealed record ReadyPayload(
        Guid TenantId,
        Guid SubjectId,
        string Kind,
        string DownloadUrl,
        string FileName,
        int RowCount,
        DateTimeOffset ExpiresAt);
}
