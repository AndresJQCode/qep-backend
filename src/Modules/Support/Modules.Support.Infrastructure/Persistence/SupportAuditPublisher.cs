using System.Text.Json;
using Modules.Audit.Domain;
using Modules.Support.Application;

namespace Modules.Support.Infrastructure.Persistence;

// Camino de auditoría atómica (ADR 0019), copia del de Loans: la entrada se acumula en SupportDbContext y commitea o revierte con
// el cambio de negocio, en la misma unidad de trabajo. No pasa por IAuditRecorder, cuya implementación acumula en
// TenancyDbContext, que SupportUnitOfWork no guarda: la fila se perdería sin error.
internal sealed class SupportAuditPublisher(SupportDbContext dbContext) : ISupportAuditPublisher
{
    private const string Source = "support";

    public void Publish(
        Guid tenantId,
        Guid actorId,
        string action,
        string resourceType,
        string resourceId,
        string outcome,
        IReadOnlyCollection<string> changedFields,
        DateTimeOffset occurredAt) =>
        dbContext.AuditEntries.Add(AuditEntry.Create(
            tenantId,
            actorId,
            AuditActorType.Human,
            action,
            resourceType,
            resourceId,
            outcome,
            JsonSerializer.Serialize(changedFields),
            Source,
            occurredAt));

    public void PublishSystem(
        Guid tenantId,
        string action,
        string resourceType,
        string resourceId,
        IReadOnlyCollection<string> changedFields,
        DateTimeOffset occurredAt) =>
        dbContext.AuditEntries.Add(AuditEntry.Create(
            tenantId,
            Guid.Empty,
            AuditActorType.System,
            action,
            resourceType,
            resourceId,
            "success",
            JsonSerializer.Serialize(changedFields),
            Source,
            occurredAt));
}
