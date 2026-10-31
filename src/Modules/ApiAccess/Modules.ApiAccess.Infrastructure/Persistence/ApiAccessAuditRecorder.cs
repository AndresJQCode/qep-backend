using System.Text.Json;
using Modules.ApiAccess.Application;
using Modules.Audit.Domain;

namespace Modules.ApiAccess.Infrastructure.Persistence;

// Camino de auditoría atómica (ADR 0019) para ApiAccess: la entrada se acumula en
// ApiAccessDbContext y commitea o revierte con el cambio. audit.entries es del módulo Audit; aquí
// se proyecta ExcludeFromMigrations, igual que Integrations.
internal sealed class ApiAccessAuditRecorder(ApiAccessDbContext dbContext) : IApiAccessAuditRecorder
{
    private const string Source = "api_access";

    public void Record(
        Guid tenantId,
        Guid actorId,
        AuditActorType actorType,
        string action,
        Guid apiKeyId,
        string outcome,
        IReadOnlyCollection<string> changedFields,
        DateTimeOffset occurredAt) =>
        dbContext.AuditEntries.Add(AuditEntry.Create(
            tenantId,
            actorId,
            actorType,
            action,
            ApiKeyAuditActions.ResourceType,
            apiKeyId.ToString(),
            outcome,
            JsonSerializer.Serialize(changedFields),
            Source,
            occurredAt));
}
