using Modules.Audit.Domain;
using Modules.Automations.Application;

namespace Modules.Automations.Infrastructure.Persistence;

// Camino de auditoría atómica (ADR 0019), copia de PipelineAuditRecorder: la entrada se acumula en AutomationsDbContext y
// commitea o revierte con el cambio. changed_fields va vacío, como en el resto de los módulos con recorder propio.
internal sealed class AutomationsAuditRecorder(AutomationsDbContext dbContext) : IAutomationsAuditRecorder
{
    private const string Source = "automations";
    private const string Outcome = "success";
    private const string NoChangedFields = "[]";

    public void Record(Guid tenantId, Guid actorId, bool bySystem, string action, string resourceType, Guid resourceId, DateTimeOffset at) =>
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
            at));
}
