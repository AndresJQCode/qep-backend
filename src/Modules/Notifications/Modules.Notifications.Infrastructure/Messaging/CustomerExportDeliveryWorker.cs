using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Notifications.Application;
using Modules.Notifications.Domain;
using Modules.Notifications.Infrastructure.Persistence;

namespace Modules.Notifications.Infrastructure.Messaging;

// Consume del outbox de plataforma el evento de exportación de clientes lista y entrega el email con el
// enlace de descarga. El enlace ya viene prefirmado en el payload: este módulo no conoce Storage ni
// sabe firmar nada. El reclamo, el lote y la cancelación son de OutboxDeliveryWorker.
internal sealed class CustomerExportDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<CustomerExportDeliveryWorker> logger)
    : OutboxDeliveryWorker(scopeFactory, logger)
{
    protected override string Consumer => "notifications.customer-export-email";

    protected override string EventName => "customers.export-ready.v1";

    protected override string TemplateRef => CustomerExportEmailTemplate.TemplateRef;

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

        // El vencimiento se muestra en la hora del tenant del export (spec 2026-09-17, punto 8b). Un
        // tenant que no resuelve falla acá, fuera del envío: el reclamo queda vivo y se reintenta, en
        // vez de mandar una hora en UTC.
        var calendar = await context.TenantClock.GetAsync(export.TenantId, stoppingToken);
        string recipient = email;
        await SendAsync(
            context,
            notification,
            () => CustomerExportEmailTemplate.Render(
                recipient, export.DownloadUrl, export.FileName, export.CustomerCount, export.ExpiresAt,
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
            root.GetProperty("customerCount").GetInt32(),
            root.GetProperty("expiresAt").GetDateTimeOffset());
    }

    private sealed record ExportPayload(
        Guid TenantId,
        Guid SubjectId,
        string DownloadUrl,
        string FileName,
        int CustomerCount,
        DateTimeOffset ExpiresAt);
}
