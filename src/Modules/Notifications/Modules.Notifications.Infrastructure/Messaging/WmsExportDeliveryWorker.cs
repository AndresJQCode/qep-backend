using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Notifications.Application;
using Modules.Notifications.Domain;
using Modules.Notifications.Infrastructure.Persistence;

namespace Modules.Notifications.Infrastructure.Messaging;

// Consume del outbox de plataforma el evento de exportación de saldos de bodega y entrega el email con el enlace de descarga.
// Modelado sobre SupplierExportDeliveryWorker (spec WMS 2026-10-31, S15), con su propio evento y su propia plantilla. El enlace ya
// viene prefirmado en el payload: este módulo no conoce Storage ni sabe firmar nada. El reclamo, el lote y la cancelación son de
// OutboxDeliveryWorker.
internal sealed class WmsExportDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<WmsExportDeliveryWorker> logger)
    : OutboxDeliveryWorker(scopeFactory, logger)
{
    protected override string Consumer => "notifications.wms-export-email";

    protected override string EventName => "wms.export-ready.v1";

    protected override string TemplateRef => WmsExportEmailTemplate.TemplateRef;

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

        // El vencimiento se muestra en la hora del tenant. Un tenant que no resuelve falla acá, fuera del envío: el reclamo queda vivo
        // y se reintenta, en vez de mandar una hora en UTC.
        var format = await ResolveEmailFormatAsync(context, export.TenantId, stoppingToken);
        string recipient = email;
        await SendAsync(
            context,
            notification,
            () => WmsExportEmailTemplate.Render(
                recipient, export.DownloadUrl, export.FileName, export.RowCount, export.ExpiresAt, format),
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
            root.GetProperty("rowCount").GetInt32(),
            root.GetProperty("expiresAt").GetDateTimeOffset());
    }

    private sealed record ExportPayload(
        Guid TenantId,
        Guid SubjectId,
        string DownloadUrl,
        string FileName,
        int RowCount,
        DateTimeOffset ExpiresAt);
}
