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
}
