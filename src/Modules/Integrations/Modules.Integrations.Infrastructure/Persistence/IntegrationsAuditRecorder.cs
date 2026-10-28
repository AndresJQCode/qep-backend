using System.Text.Json;
using Modules.Audit.Domain;
using Modules.Integrations.Application;

namespace Modules.Integrations.Infrastructure.Persistence;

// Camino de auditoría atómica (ADR 0019) para Integrations: la entrada se acumula en
// IntegrationsDbContext y commitea o revierte con el cambio. audit.entries es del módulo Audit;
// acá se proyecta ExcludeFromMigrations, igual que Identity y Tenancy (P1).
internal sealed class IntegrationsAuditRecorder(IntegrationsDbContext dbContext) : IIntegrationsAuditRecorder
{
    private const string Source = "integrations";

    public void Record(
        Guid tenantId,
        Guid actorId,
        AuditActorType actorType,
        string action,
        Guid connectionId,
        string outcome,
        IReadOnlyCollection<string> changedFields,
        DateTimeOffset occurredAt) =>
        dbContext.AuditEntries.Add(AuditEntry.Create(
            tenantId,
            actorId,
            actorType,
            action,
            ConnectionAuditActions.ResourceType,
            connectionId.ToString(),
            outcome,
            JsonSerializer.Serialize(changedFields),
            Source,
            occurredAt));
}
