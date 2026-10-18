using Modules.Audit.Domain;
using Modules.Shopify.Application;

namespace Modules.Shopify.Infrastructure.Persistence;

// Camino de auditoría atómica (ADR 0019), copia de IntegrationsAuditRecorder: la entrada se acumula en ShopifyDbContext y commitea o
// revierte con el cambio. audit.entries es del módulo Audit; acá se proyecta ExcludeFromMigrations (spec 2026-10-15 §9, H19).
internal sealed class ShopifyAuditRecorder(ShopifyDbContext dbContext) : IShopifyAuditRecorder
{
    private const string Source = "shopify";
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
