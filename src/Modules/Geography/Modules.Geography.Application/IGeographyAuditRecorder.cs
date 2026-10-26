using Modules.Audit.Domain;

namespace Modules.Geography.Application;

/// <summary>
/// Auditoría atómica de Geography (ADR 0019): la entrada se acumula en <c>GeographyDbContext</c> y commitea con el cambio.
/// Deliberadamente no es el <c>IAuditRecorder</c> compartido, que ya está ligado a <c>TenancyDbContext</c>: una segunda
/// ligadura le robaría la auditoría a Tenancy (precedentes <c>IShopifyAuditRecorder</c> e <c>IIntegrationsAuditRecorder</c>).
/// La fuente es <c>geography</c> y el resultado <c>success</c>.
/// </summary>
public interface IGeographyAuditRecorder
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
