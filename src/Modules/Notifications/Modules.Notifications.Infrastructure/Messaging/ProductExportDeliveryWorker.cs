using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Notifications.Application;
using Modules.Notifications.Domain;
using Modules.Notifications.Infrastructure.Persistence;

namespace Modules.Notifications.Infrastructure.Messaging;

// Consume del outbox de plataforma el evento de exportación del catálogo lista y entrega el email con el
// enlace de descarga, que ya viene prefirmado en el payload. El reclamo, el lote y la cancelación son
// de OutboxDeliveryWorker.
internal sealed class ProductExportDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<ProductExportDeliveryWorker> logger)
    : OutboxDeliveryWorker(scopeFactory, logger)
{
    protected override string Consumer => "notifications.product-export-email";

    protected override string EventName => "catalog.product-export-ready.v1";

    protected override string TemplateRef => ProductExportEmailTemplate.TemplateRef;

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

        // El vencimiento se muestra en la hora del tenant del export (spec 2026-09-17, punto 8b).
        var calendar = await context.TenantClock.GetAsync(export.TenantId, stoppingToken);
        string recipient = email;
        await SendAsync(
            context,
            notification,
            () => ProductExportEmailTemplate.Render(
                recipient, export.DownloadUrl, export.FileName, export.ProductCount, export.ExpiresAt,
                calendar.TimeZone),
            stoppingToken);
        return notification;
    }

    private static ExportPayload ParsePayload(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        var root = document.RootElement;
        return new ExportPayload(
            root.GetProperty("tenantId").GetGuid(),
            root.GetProperty("subjectId").GetGuid(),
            root.GetProperty("downloadUrl").GetString() ?? string.Empty,
            root.GetProperty("fileName").GetString() ?? string.Empty,
            root.GetProperty("productCount").GetInt32(),
            root.GetProperty("expiresAt").GetDateTimeOffset());
    }

    private sealed record ExportPayload(
        Guid TenantId,
        Guid SubjectId,
        string DownloadUrl,
        string FileName,
        int ProductCount,
        DateTimeOffset ExpiresAt);
}
