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

    // Spec 2026-11-13 (D8): las asociaciones de líneas personalizadas de una tienda. El recurso es la asociación y el detalle lleva
    // la conexión, el título y la variante.
    public const string CustomLineMappingCreated = "shopify.custom_line_mapping.created";

    public const string CustomLineMappingDeleted = "shopify.custom_line_mapping.deleted";

    public const string CustomLineMappingResourceType = "shopify_custom_line_mapping";

    // Spec 2026-11-14 §4: las decisiones de una persona sobre un cambio de Shopify. Aplicar uno solo (tienda automática) lo
    // audita Quotations como integración (quotation.order.external_edit_applied).
    public const string OrderEditAccepted = "shopify.order_edit.accepted";

    public const string OrderEditRejected = "shopify.order_edit.rejected";

    public const string OrderEditReviewed = "shopify.order_edit.reviewed";

    public const string OrderEditResourceType = "shopify_order_edit";

    /// <summary>Spec 2026-11-14 §5: el asesor corrigió envío, nota o atributos de un pedido de Shopify y quedó encolado el envío. El
    /// recurso es la fila del espejo y el detalle lleva los grupos (<c>{ "groups": [...] }</c>).</summary>
    public const string OrderCorrected = "shopify.order.corrected";

    public const string ShopifyOrderResourceType = "shopify_order";

    /// <summary>Owner 2026-11-14: alguien soltó el envío retenido de un pedido («Sincronizar con Shopify»). Recurso: la fila del
    /// espejo; detalle: los grupos.</summary>
    public const string OrderPushReleased = "shopify.order.push_released";

    /// <summary>Owner 2026-11-14: alguien soltó los envíos retenidos de una tienda. Recurso: la conexión; detalle:
    /// <c>{ "released": n }</c>. Sin envíos retenidos no se audita nada.</summary>
    public const string StorePushesReleased = "shopify.store.pushes_released";

    /// <summary>Spec 2026-11-17 §7: alguien pidió traer los productos de una tienda. Recurso: la importación; detalle: la conexión y
    /// el modo.</summary>
    public const string ProductImportRequested = "shopify.products.import_requested";

    public const string ProductImportResourceType = "shopify_product_import";

    /// <summary>Spec 2026-11-17 §5.5: alguien desvinculó un producto de Shopify y quedó propio de QEP. Recurso: el producto de
    /// Catalog; detalle: las conexiones y cuántos vínculos se quitaron.</summary>
    public const string ProductUnlinked = "shopify.products.unlinked";

    public const string ProductResourceType = "product";

    /// <summary>Spec 2026-11-17 (onboarding) §4.2: alguien eligió «Seguir así» con la cobertura del catálogo. Recurso: la conexión
    /// (<see cref="ConnectionResourceType"/>); detalle: <c>[]</c>.</summary>
    public const string ProductsCoverageAccepted = "shopify.products.coverage_accepted";
}
