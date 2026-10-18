namespace Modules.Shopify.Application;

/// <summary>Spec 2026-10-15 §5.5: las acciones de auditoría del módulo. <see cref="ComplianceCustomersRedact"/> no tiene nombre en
/// el spec («se audita igual»): queda como decisión a ratificar.</summary>
public static class ShopifyAuditActions
{
    public const string ComplianceDataRequest = "shopify.compliance.data_request";
    public const string ComplianceCustomersRedact = "shopify.compliance.customers_redact";

    /// <summary>El recurso de las entradas de cumplimiento: la conexión de la tienda.</summary>
    public const string ConnectionResourceType = "integration_connection";
}
