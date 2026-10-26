using Modules.Audit.Domain;
using Modules.Geography.Application;

namespace Modules.Geography.Infrastructure.Persistence;

// Camino de auditoría atómica (ADR 0019), copia de ShopifyAuditRecorder: la entrada se acumula en GeographyDbContext y commitea o
// revierte con el cambio. audit.entries es del módulo Audit; acá se proyecta ExcludeFromMigrations.
internal sealed class GeographyAuditRecorder(GeographyDbContext dbContext) : IGeographyAuditRecorder
{
    private const string Source = "geography";
    private const string Outcome = "success";

    public void Record(
        Guid tenantId,
        Guid actorId,
        AuditActorType actorType,
        string action,
        string resourceType,
        string resourceId,
        string changedFieldsJson,
        DateTimeOffset occurredAt) =>
        dbContext.AuditEntries.Add(AuditEntry.Create(
            tenantId,
            actorId,
            actorType,
            action,
            resourceType,
            resourceId,
            Outcome,
            changedFieldsJson,
            Source,
            occurredAt));
}
