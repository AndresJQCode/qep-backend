namespace Modules.Quotations.Application;

/// <summary>
/// Spec 2026-11-18 §5.5: ¿hay una factura de un facturador externo todavía saliendo o sin resolver para el pedido? Quotations declara el
/// puerto y Bootstrapper lo implementa leyendo <c>siigo.invoices</c> (<c>Pending</c> o <c>Unknown</c>), así Quotations no referencia a
/// Siigo. Con una abierta, la marca manual responde <c>409 order.order.external_invoice_pending</c>: nadie marca a mano mientras la
/// factura real puede estar saliendo.
/// </summary>
public interface IExternalInvoiceStatus
{
    Task<bool> HasOpenInvoiceAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken);
}
