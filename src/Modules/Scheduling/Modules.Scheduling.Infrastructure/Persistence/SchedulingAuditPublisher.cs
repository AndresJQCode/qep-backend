using System.Text.Json;
using Modules.Audit.Domain;
using Modules.Scheduling.Application;

namespace Modules.Scheduling.Infrastructure.Persistence;

// Camino de auditoría atómica (ADR 0019), copia del de Hr: la entrada se acumula en SchedulingDbContext y commitea o revierte con el
// cambio de negocio, en la misma unidad de trabajo. No pasa por IAuditRecorder, cuya implementación acumula en TenancyDbContext, que
// SchedulingUnitOfWork no guarda: la fila se perdería sin error.
internal sealed class SchedulingAuditPublisher(SchedulingDbContext dbContext) : ISchedulingAuditPublisher
{
    private const string Source = "scheduling";

    public void Publish(
        Guid tenantId,
        Guid actorId,
        string action,
        string resourceType,
        string resourceId,
        string outcome,
        IReadOnlyCollection<string> changedFields,
        DateTimeOffset occurredAt) =>
        Add(tenantId, actorId, AuditActorType.Human, action, resourceType, resourceId, outcome, changedFields, occurredAt);

    public void PublishByAgent(
        Guid tenantId,
        Guid agentId,
        string action,
        string resourceType,
        string resourceId,
        string outcome,
        IReadOnlyCollection<string> changedFields,
        DateTimeOffset occurredAt) =>
        Add(tenantId, agentId, AuditActorType.AiAgent, action, resourceType, resourceId, outcome, changedFields, occurredAt);

    private void Add(
        Guid tenantId,
        Guid actorId,
        AuditActorType actorType,
        string action,
        string resourceType,
        string resourceId,
        string outcome,
        IReadOnlyCollection<string> changedFields,
        DateTimeOffset occurredAt) =>
        dbContext.AuditEntries.Add(AuditEntry.Create(
            tenantId,
            actorId,
            actorType,
            action,
            resourceType,
            resourceId,
            outcome,
            JsonSerializer.Serialize(changedFields),
            Source,
            occurredAt));
}
