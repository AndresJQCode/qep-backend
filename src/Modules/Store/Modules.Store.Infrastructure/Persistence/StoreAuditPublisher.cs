using System.Text.Json;
using Modules.Audit.Domain;
using Modules.Store.Application;

namespace Modules.Store.Infrastructure.Persistence;

// Camino de auditoría atómica (ADR 0019), copia del de Loans: la entrada se acumula en StoreDbContext y commitea o revierte con el
// cambio de negocio. No pasa por IAuditRecorder, cuya implementación acumula en TenancyDbContext, que StoreUnitOfWork no guarda: la
// fila se perdería sin error.
internal sealed class StoreAuditPublisher(StoreDbContext dbContext) : IStoreAuditPublisher
{
    private const string Source = "store";

    private const string SuccessOutcome = "success";

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

    // Sin sesión ni persona: el actor es la propia tienda del tenant, con tipo Integration (como la conexión de Shopify).
    public void PublishAnonymous(Guid tenantId, string action, string resourceType, string resourceId, DateTimeOffset at) =>
        dbContext.AuditEntries.Add(AuditEntry.Create(
            tenantId,
            tenantId,
            AuditActorType.Integration,
            action,
            resourceType,
            resourceId,
            SuccessOutcome,
            "[]",
            Source,
            at));
}
