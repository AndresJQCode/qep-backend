namespace Modules.Quotations.Application;

/// <summary>
/// Spec 2026-11-18 §5.5: ¿hay una factura viva de un facturador externo para el pedido (saliendo, sin resolver o ya creada)? Quotations
/// declara el puerto y Bootstrapper lo implementa leyendo <c>siigo.invoices</c> (todo estado menos <c>Rejected</c>, <c>Failed</c> y
/// <c>Annulled</c>), así Quotations no referencia a Siigo. Con una viva, la marca manual responde
/// <c>409 order.order.external_invoice_pending</c>: nadie marca a mano mientras la factura real existe o puede estar saliendo.
/// </summary>
public interface IExternalInvoiceStatus
{
    Task<bool> HasOpenInvoiceAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken);
}
