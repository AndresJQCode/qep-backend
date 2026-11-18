using System.Text.Json;
using Modules.Audit.Domain;
using Modules.Purchases.Application;

namespace Modules.Purchases.Infrastructure.Persistence;

// Camino de auditoría atómica (ADR 0019), copia del de Loans: la entrada se acumula en PurchasesDbContext y se confirma o revierte con
// el cambio de negocio, en la misma unidad de trabajo. No pasa por IAuditRecorder, cuya implementación acumula en TenancyDbContext,
// que PurchasesUnitOfWork no guarda: la fila se perdería sin error.
internal sealed class PurchasesAuditPublisher(PurchasesDbContext dbContext) : IPurchasesAuditPublisher
{
    private const string Source = "purchases";

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
