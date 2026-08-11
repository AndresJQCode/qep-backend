using System.Text.Json;
using Modules.Catalog.Application;

namespace Modules.Catalog.Infrastructure.Persistence;

// Buffers a platform.audit.recorded.v1 event into the CatalogDbContext outbox projection so
// it commits atomically with the catalogue change. The Audit module projection worker writes
// it to audit.entries.
internal sealed class CatalogAuditPublisher(CatalogDbContext dbContext) : ICatalogAuditPublisher
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
            "product",
            resourceId,
            outcome,
            [],
            "catalog",
            occurredAt));

        dbContext.Outbox.Add(new CatalogOutboxMessage
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
