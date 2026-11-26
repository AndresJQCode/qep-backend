namespace Modules.Siigo.Application;

/// <summary>Spec 2026-11-18 §5.7: las acciones de auditoría de Siigo. Son contrato con la pantalla de auditoría: no se renombran.</summary>
public static class SiigoAuditActions
{
    /// <summary>Se guardó un paso de la configuración de una conexión; lleva los pasos cambiados, sin valores de Siigo.</summary>
    public const string SettingsUpdated = "siigo.settings.updated";

    public const string InvoiceCreated = "siigo.invoice.created";

    public const string InvoiceRejected = "siigo.invoice.rejected";

    /// <summary>La factura quedó por confirmar (spec 2026-11-24): la primera vez que no se sabe si salió, o cuando un rechazo se retiene
    /// por un envío anterior sin respuesta.</summary>
    public const string InvoiceUnanswered = "siigo.invoice.unanswered";

    /// <summary>Siigo rechazó los pagos por una diferencia de redondeo (hasta un peso) y la factura salió otra vez con su total.</summary>
    public const string InvoicePaymentsAdjusted = "siigo.invoice.payments_adjusted";

    public const string InvoiceResolved = "siigo.invoice.resolved";

    /// <summary>Se verificó en Siigo que la factura se anuló o se borró (spec 2026-11-25, anulación): una por fila, con <c>outcome</c>
    /// <c>annulled</c> o <c>deleted</c> y el estado del que venía.</summary>
    public const string InvoiceAnnulled = "siigo.invoice.annulled";

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
