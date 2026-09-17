using System.Text.Json;
using Modules.Storage.Application;

namespace Modules.Storage.Infrastructure.Persistence;

// Auditoría operativa (ADR 0019): acumula un evento platform.audit.recorded.v1 en la
// proyección de outbox de StorageDbContext para que commitee atómico con la operación de
// archivo. El worker de proyección del módulo Audit lo escribe en audit.entries.
internal sealed class StorageAuditPublisher(StorageDbContext dbContext) : IStorageAuditPublisher
{
    private const string EventName = "platform.audit.recorded.v1";

    public void Publish(
        Guid tenantId,
        Guid actorId,
        string action,
        string resourceId,
        string outcome,
        DateTimeOffset occurredAt) =>
        Add(
            JsonSerializer.Serialize(new AuditEventPayload(
                tenantId,
                actorId,
                "Human",
                action,
                "file",
                resourceId,
                outcome,
                [],
                "storage",
                occurredAt)),
            occurredAt);

    public void PublishSystem(
        Guid? tenantId,
        string action,
        string resourceType,
        string resourceId,
        string outcome,
        DateTimeOffset occurredAt) =>
        Add(
            JsonSerializer.Serialize(new SystemAuditEventPayload(
                tenantId,
                Guid.Empty,
                "System",
                action,
                resourceType,
                resourceId,
                outcome,
                [],
                "storage",
                occurredAt)),
            occurredAt);

    private void Add(string payload, DateTimeOffset occurredAt) =>
        dbContext.Outbox.Add(new StorageOutboxMessage
        {
            Id = Guid.CreateVersion7(),
            EventName = EventName,
            PayloadJson = payload,
            CorrelationId = Guid.NewGuid().ToString(),
            OccurredAt = occurredAt,
        });

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

    // tenantId nullable: AuditProjectionWorker lo acepta ausente o null.
    private sealed record SystemAuditEventPayload(
        Guid? tenantId,
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
