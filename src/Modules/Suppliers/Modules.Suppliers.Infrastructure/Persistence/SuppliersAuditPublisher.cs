using System.Text.Json;
using Modules.Suppliers.Application;

namespace Modules.Suppliers.Infrastructure.Persistence;

// Acumula un evento platform.audit.recorded.v1 en la proyeccion de outbox de SuppliersDbContext
// para que commitee atomico con el cambio. El worker de proyeccion del modulo Audit lo escribe en
// audit.entries.
internal sealed class SuppliersAuditPublisher(SuppliersDbContext dbContext)
    : ISuppliersAuditPublisher
{
    private const string EventName = "platform.audit.recorded.v1";

    public void Publish(
        Guid tenantId,
        Guid actorId,
        string action,
        string resourceId,
        string outcome,
        DateTimeOffset occurredAt)
    {
        var payload = JsonSerializer.Serialize(new AuditEventPayload(
            tenantId,
            actorId,
            "Human",
            action,
            "supplier",
            resourceId,
            outcome,
            [],
            "suppliers",
            occurredAt));

        dbContext.Outbox.Add(new SuppliersOutboxMessage
        {
            Id = Guid.CreateVersion7(),
            EventName = EventName,
            PayloadJson = payload,
            CorrelationId = Guid.NewGuid().ToString(),
            OccurredAt = occurredAt,
        });
    }

    private sealed record AuditEventPayload(
        Guid tenantId,
        Guid actorId,
        string actorType,
        string action,
        string resourceType,
        string resourceId,
        string outcome,
        IReadOnlyCollection<string> changedFields,
        string source,
        DateTimeOffset occurredAt);
}
