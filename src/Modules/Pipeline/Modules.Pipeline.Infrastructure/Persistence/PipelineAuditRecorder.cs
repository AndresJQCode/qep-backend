using Modules.Audit.Domain;
using Modules.Pipeline.Application;

namespace Modules.Pipeline.Infrastructure.Persistence;

// Camino de auditoría atómica (ADR 0019), copia de MessagingAuditRecorder: la entrada se acumula en PipelineDbContext y
// commitea o revierte con el cambio. changed_fields va vacío, como en el resto de los módulos con recorder propio.
internal sealed class PipelineAuditRecorder(PipelineDbContext dbContext) : IPipelineAuditRecorder
{
    private const string Source = "pipeline";
    private const string Outcome = "success";
    private const string NoChangedFields = "[]";

    public void Record(Guid tenantId, Guid actorId, bool bySystem, string action, string resourceType, Guid resourceId, DateTimeOffset occurredAt) =>
        dbContext.AuditEntries.Add(AuditEntry.Create(
            tenantId,
            actorId,
            bySystem ? AuditActorType.System : AuditActorType.Human,
            action,
            resourceType,
            resourceId.ToString(),
            Outcome,
            NoChangedFields,
            Source,
            occurredAt));
}
