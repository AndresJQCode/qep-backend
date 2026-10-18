namespace Modules.Shopify.Application;

/// <summary>Spec 2026-10-15 §5.5: las acciones de auditoría del módulo. <see cref="ComplianceCustomersRedact"/> no tiene nombre en
/// el spec («se audita igual»): queda como decisión a ratificar.</summary>
public static class ShopifyAuditActions
{
    public const string ComplianceDataRequest = "shopify.compliance.data_request";
    public const string ComplianceCustomersRedact = "shopify.compliance.customers_redact";

    /// <summary>El <c>PUT</c> de la configuración de la tienda, como todo <c>*.updated</c> de configuración del repo
    /// (<c>quotations.settings.updated</c>, <c>quotations.carrier.updated</c>). Cambia la cuenta de cobro y el asesor. El nombre
    /// sigue la convención; el owner lo puede renombrar.</summary>
    public const string StoreUpdated = "shopify.store.updated";

    /// <summary>El recurso de las entradas de cumplimiento: la conexión de la tienda.</summary>
    public const string ConnectionResourceType = "integration_connection";
}
