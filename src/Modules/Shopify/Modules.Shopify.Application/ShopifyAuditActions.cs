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

    // Spec 2026-10-26 D-C7 y desvío S9: la corrección de una ciudad por un sinónimo, en la auditoría del tenant del pedido o del
    // cliente, con actor de sistema y el operador y el sinónimo en el detalle. «resolved» es la primera vez (se confirmó el
    // sinónimo); «corrected», cuando el operador le cambió la ciudad al sinónimo (§4.6).
    public const string OrderCityResolved = "shopify.order.city_resolved";
    public const string CustomerCityResolved = "shopify.customer.city_resolved";
    public const string OrderCityCorrected = "shopify.order.city_corrected";
    public const string CustomerCityCorrected = "shopify.customer.city_corrected";

    /// <summary>El recurso de esas entradas: el pedido de QEP (Quotations audita todo como <c>quotation</c>, pero acá lo que cambió
    /// es el pedido) o el cliente, con el mismo nombre que le da Customers.</summary>
    public const string OrderResourceType = "order";

    public const string CustomerResourceType = "customer";
}
