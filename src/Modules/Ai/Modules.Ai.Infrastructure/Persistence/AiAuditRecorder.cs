using Modules.Ai.Application;
using Modules.Audit.Domain;

namespace Modules.Ai.Infrastructure.Persistence;

// Camino de auditoría atómica (ADR 0019), copia de AutomationsAuditRecorder: la entrada se acumula en AiDbContext y commitea o revierte
// con el cambio. changed_fields va vacío, como en el resto de los módulos con recorder propio.
internal sealed class AiAuditRecorder(AiDbContext dbContext) : IAiAuditRecorder
{
    internal const string Source = "ai";
    private const string Outcome = "success";
    private const string NoChangedFields = "[]";

    public void Record(Guid tenantId, Guid actorId, string action, Guid agentId, DateTimeOffset at) =>
        dbContext.AuditEntries.Add(AuditEntry.Create(
            tenantId,
            actorId,
            AuditActorType.Human,
            action,
            AiAuditActions.ResourceAgent,
            agentId.ToString(),
            Outcome,
            NoChangedFields,
            Source,
            at));

    public void RecordCopilot(Guid tenantId, Guid? actorId, string action, string resourceType, Guid resourceId, DateTimeOffset at) =>
        dbContext.AuditEntries.Add(AuditEntry.Create(
            tenantId,
            actorId ?? Guid.Empty,
            actorId is null ? AuditActorType.System : AuditActorType.Human,
            action,
            resourceType,
            resourceId.ToString(),
            Outcome,
            NoChangedFields,
            Source,
            at));

    public void RecordByAgent(Guid tenantId, string action, Guid agentId, DateTimeOffset at) =>
        dbContext.AuditEntries.Add(AuditEntry.Create(
            tenantId,
            agentId,
            AuditActorType.AiAgent,
            action,
            AiAuditActions.ResourceAgent,
            agentId.ToString(),
            Outcome,
            NoChangedFields,
            Source,
            at));
}
