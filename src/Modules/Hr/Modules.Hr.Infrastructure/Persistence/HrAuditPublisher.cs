using System.Text.Json;
using Modules.Audit.Domain;
using Modules.Hr.Application;

namespace Modules.Hr.Infrastructure.Persistence;

// Camino de auditoría atómica (ADR 0019), copia del de Loans: la entrada se acumula en HrDbContext y commitea o revierte con el
// cambio de negocio, en la misma unidad de trabajo. No pasa por IAuditRecorder, cuya implementación acumula en TenancyDbContext,
// que HrUnitOfWork no guarda: la fila se perdería sin error.
internal sealed class HrAuditPublisher(HrDbContext dbContext) : IHrAuditPublisher
{
    private const string Source = "hr";

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
}
