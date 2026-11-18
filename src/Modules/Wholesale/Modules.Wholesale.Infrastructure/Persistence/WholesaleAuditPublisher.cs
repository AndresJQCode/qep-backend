using System.Text.Json;
using Modules.Audit.Domain;
using Modules.Wholesale.Application;

namespace Modules.Wholesale.Infrastructure.Persistence;

// Camino de auditoría atómica (ADR 0019), copia del de Purchases: la entrada se acumula en WholesaleDbContext y se confirma o
// revierte con el cambio de negocio, en la misma unidad de trabajo. No pasa por IAuditRecorder, que acumula en TenancyDbContext:
// la fila se perdería sin error.
internal sealed class WholesaleAuditPublisher(WholesaleDbContext dbContext) : IWholesaleAuditPublisher
{
    private const string Source = "wholesale";

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
