using System.Text.Json;
using Modules.Customers.Application;

namespace Modules.Customers.Infrastructure.Persistence;

// Acumula el aviso de exportacion lista en la proyeccion de outbox de CustomersDbContext, para que
// commitee atomico con la auditoria. Lo consume CustomerExportDeliveryWorker en Notifications, que
// resuelve el correo del solicitante y manda el mensaje.
internal sealed class CustomerExportEventPublisher(CustomersDbContext dbContext)
    : ICustomerExportEventPublisher
{
    private const string EventName = "customers.export-ready.v1";

    public void Publish(
        Guid tenantId,
        Guid subjectId,
        string downloadUrl,
        string fileName,
        int customerCount,
        DateTimeOffset expiresAt,
        DateTimeOffset occurredAt)
    {
        var payload = JsonSerializer.Serialize(new ExportReadyPayload(
            tenantId, subjectId, downloadUrl, fileName, customerCount, expiresAt));

        dbContext.Outbox.Add(new CustomersOutboxMessage
        {
            Id = Guid.CreateVersion7(),
            EventName = EventName,
            PayloadJson = payload,
            CorrelationId = Guid.NewGuid().ToString(),
            OccurredAt = occurredAt,
        });
    }

    // Nombres en minuscula como el resto de los payloads del outbox: el consumidor los lee por
    // nombre con JsonDocument, sin opciones de serializacion de por medio.
    private sealed record ExportReadyPayload(
        Guid tenantId,
        Guid subjectId,
        string downloadUrl,
        string fileName,
        int customerCount,
        DateTimeOffset expiresAt);
}
