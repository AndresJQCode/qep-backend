using Modules.Audit.Domain;

namespace Modules.Shopify.Application;

/// <summary>
/// Auditoría atómica de Shopify (ADR 0019, spec 2026-10-15 §9): la entrada se acumula en <c>ShopifyDbContext</c> y commitea con el
/// cambio. Deliberadamente no es el <c>IAuditRecorder</c> compartido, que ya está ligado a <c>TenancyDbContext</c>: una segunda
/// ligadura le robaría la auditoría a Tenancy (precedentes <c>IIntegrationsAuditRecorder</c> e <c>IIdentityAuditRecorder</c>).
/// <paramref name="changedFieldsJson"/> lleva claves e ids, nunca tokens ni secretos (§12).
/// </summary>
public interface IShopifyAuditRecorder
{
    void Record(
        Guid tenantId,
        Guid actorId,
        AuditActorType actorType,
        string action,
        string resourceType,
        string resourceId,
        string changedFieldsJson,
        DateTimeOffset occurredAt);
}
