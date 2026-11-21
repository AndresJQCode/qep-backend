using Modules.Audit.Domain;

namespace Modules.Coordinadora.Application;

/// <summary>Spec 2026-11-21 §5.6: las acciones de auditoría de Coordinadora. Son contrato con la pantalla de auditoría: no se renombran.</summary>
public static class CoordinadoraAuditActions
{
    /// <summary>Se guardó la configuración de la conexión; sin valores.</summary>
    public const string SettingsUpdated = "coordinadora.settings.updated";

    public const string ShipmentCreated = "coordinadora.shipment.created";

    public const string ShipmentRejected = "coordinadora.shipment.rejected";

    public const string ShipmentCancelled = "coordinadora.shipment.cancelled";

    public const string ShipmentResolved = "coordinadora.shipment.resolved";

    /// <summary>El recurso de las entradas de configuración: la conexión de Integrations (con el mismo nombre que le dan Shopify y Siigo).</summary>
    public const string ConnectionResourceType = "integration_connection";

    /// <summary>El recurso de las entradas de guía: la fila de <c>coordinadora.shipments</c>.</summary>
    public const string ShipmentResourceType = "coordinadora_shipment";
}

/// <summary>
/// Auditoría atómica de Coordinadora (ADR 0019): la entrada se acumula en <c>CoordinadoraDbContext</c> y commitea con el cambio.
/// Deliberadamente no es el <c>IAuditRecorder</c> compartido, que ya está ligado a <c>TenancyDbContext</c>: una segunda ligadura le
/// robaría la auditoría a Tenancy (precedentes <c>IShopifyAuditRecorder</c> e <c>ISiigoAuditRecorder</c>). <paramref name="changedFieldsJson"/>
/// lleva ids y estados, nunca las claves ni el destinatario.
/// </summary>
public interface ICoordinadoraAuditRecorder
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
