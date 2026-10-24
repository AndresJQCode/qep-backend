using System.Text.Json;
using Modules.Suppliers.Application;

namespace Modules.Suppliers.Infrastructure.Persistence;

// Acumula el aviso de exportación lista en la proyección de outbox de SuppliersDbContext, para que
// commitee atómico con la auditoría. Lo consume el worker de entrega de Notifications (SUP-08).
internal sealed class SupplierExportEventPublisher(SuppliersDbContext dbContext)
    : ISupplierExportEventPublisher
{
    private const string EventName = "suppliers.export-ready.v1";

    public void Publish(
        Guid tenantId,
        Guid subjectId,
        string downloadUrl,
        string fileName,
        int supplierCount,
        DateTimeOffset expiresAt,
        DateTimeOffset occurredAt)
    {
        var payload = JsonSerializer.Serialize(new ExportReadyPayload(
            tenantId, subjectId, downloadUrl, fileName, supplierCount, expiresAt));

        dbContext.Outbox.Add(new SuppliersOutboxMessage
        {
            Id = Guid.CreateVersion7(),
            EventName = EventName,
            PayloadJson = payload,
            CorrelationId = Guid.NewGuid().ToString(),
            OccurredAt = occurredAt,
        });
    }

    // Nombres en minúscula como el resto de los payloads del outbox: el consumidor los lee por
    // nombre con JsonDocument, sin opciones de serialización de por medio.
    private sealed record ExportReadyPayload(
        Guid tenantId,
        Guid subjectId,
        string downloadUrl,
        string fileName,
        int supplierCount,
        DateTimeOffset expiresAt);
}
