using System.Text.Json;
using Modules.Pos.Application;

namespace Modules.Pos.Infrastructure.Persistence;

internal sealed class PosAuditPublisher(PosDbContext dbContext) : IPosAuditPublisher
{
    private const string EventName = "platform.audit.recorded.v1";

    public void Publish(
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
            tenantId, actorId, "Human", action, resourceType, resourceId, outcome, changedFields, "pos", occurredAt));

        dbContext.Outbox.Add(new PosOutboxMessage
        {
            Id = Guid.CreateVersion7(),
            EventName = EventName,
            PayloadJson = payload,
            CorrelationId = Guid.NewGuid().ToString(),
            OccurredAt = occurredAt,
        });
    }

    // Nombres en minúscula como el resto de los payloads del outbox.
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
