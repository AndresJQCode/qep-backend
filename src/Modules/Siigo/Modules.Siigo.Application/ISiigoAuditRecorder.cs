using Modules.Audit.Domain;

namespace Modules.Siigo.Application;

/// <summary>
/// Auditoría atómica de Siigo (ADR 0019, spec 2026-11-18 §5.7): la entrada se acumula en <c>SiigoDbContext</c> y commitea con el
/// cambio. Deliberadamente no es el <c>IAuditRecorder</c> compartido, que ya está ligado a <c>TenancyDbContext</c>: una segunda
/// ligadura le robaría la auditoría a Tenancy (precedentes <c>IShopifyAuditRecorder</c> e <c>IIntegrationsAuditRecorder</c>).
/// <paramref name="changedFieldsJson"/> lleva claves e ids, nunca la clave de acceso, el token ni el cuerpo que se mandó a Siigo.
/// </summary>
public interface ISiigoAuditRecorder
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
