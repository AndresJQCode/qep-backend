using System.Text.Json;
using Modules.Catalog.Application;

namespace Modules.Catalog.Infrastructure.Persistence;

// Acumula un evento platform.audit.recorded.v1 en la proyección de outbox de CatalogDbContext
// para que commitee atómico con el cambio del catálogo. El worker de proyección del módulo
// Audit lo escribe en audit.entries.
internal sealed class CatalogAuditPublisher(CatalogDbContext dbContext) : ICatalogAuditPublisher
{
    private const string EventName = "platform.audit.recorded.v1";

    public void Publish(
        Guid tenantId,
        Guid actorId,
        string action,
        string resourceId,
        string outcome,
        DateTimeOffset occurredAt) =>
        Write(tenantId, actorId, action, "product", resourceId, outcome, [], occurredAt);

    public void PublishResource(
        Guid tenantId,
        Guid actorId,
        string action,
        string resourceType,
        string resourceId,
        IReadOnlyCollection<string> changedFields,
        DateTimeOffset occurredAt) =>
        Write(tenantId, actorId, action, resourceType, resourceId, "success", changedFields, occurredAt);

    private void Write(
        Guid tenantId,
        Guid actorId,
        string action,
        string resourceType,
        string resourceId,
        string outcome,
        IReadOnlyCollection<string> changedFields,
        DateTimeOffset occurredAt)
    {
        var payload = JsonSerializer.Serialize(new AuditEventPayload(
            tenantId,
            actorId,
            "Human",
            action,
            resourceType,
            resourceId,
            outcome,
            changedFields,
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
