namespace Modules.Siigo.Application;

/// <summary>Spec 2026-11-18 §5.7: las acciones de auditoría de Siigo. Son contrato con la pantalla de auditoría: no se renombran.</summary>
public static class SiigoAuditActions
{
    /// <summary>Se guardó un paso de la configuración de una conexión; lleva los pasos cambiados, sin valores de Siigo.</summary>
    public const string SettingsUpdated = "siigo.settings.updated";

    public const string InvoiceCreated = "siigo.invoice.created";

    public const string InvoiceRejected = "siigo.invoice.rejected";

    public const string InvoiceResolved = "siigo.invoice.resolved";

    /// <summary>Se mandó el PDF de la factura al WhatsApp del cliente (spec 2026-11-21). Sólo el pedido, el tipo, el estado y el número:
    /// nunca el teléfono, la URL ni el contenido.</summary>
    public const string InvoiceWhatsAppSent = "siigo.invoice.whatsapp_sent";

    /// <summary>Se pidió una tanda de facturación; lleva cuántos pedidos, nunca cuáles facturas salieron (esas llevan la suya).</summary>
    public const string InvoiceBatchRequested = "siigo.invoice_batch.requested";

    /// <summary>El recurso de las entradas de configuración: la conexión de Integrations (con el mismo nombre que le dan Shopify y
    /// Messaging).</summary>
    public const string ConnectionResourceType = "integration_connection";

    /// <summary>El recurso de las entradas de factura: la fila de <c>siigo.invoices</c>.</summary>
    public const string InvoiceResourceType = "siigo_invoice";

    /// <summary>El recurso de las entradas de tanda: la fila de <c>siigo.invoice_batches</c>.</summary>
    public const string InvoiceBatchResourceType = "siigo_invoice_batch";
}
