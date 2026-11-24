using Modules.Quotations.Application;
using Modules.Siigo.Application;

namespace Bootstrapper;

/// <summary>
/// El adaptador de <see cref="ISiigoInvoiceWhatsApp"/> (spec 2026-11-21) sobre <see cref="IInvoiceWhatsAppDelivery"/> de Quotations. Vive en
/// Bootstrapper porque Siigo no referencia a Quotations (<c>SiigoLayerTests</c>) y sólo reenvía: el canal, el teléfono, la plantilla y la
/// copia pública son de Quotations, así que <c>WhatsAppChannelSelection</c> no se reimplementa acá.
/// </summary>
internal sealed class SiigoInvoiceWhatsApp(IInvoiceWhatsAppDelivery delivery) : ISiigoInvoiceWhatsApp
{
    public Task SendAsync(
        Guid tenantId, Guid orderId, string invoiceNumber, decimal? invoiceTotal, ReadOnlyMemory<byte> pdf, CancellationToken cancellationToken) =>
        delivery.SendAsync(new InvoiceWhatsAppRequest(tenantId, orderId, invoiceNumber, invoiceTotal, pdf), cancellationToken);

    public Task<bool> CanSendAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken) =>
        delivery.CanSendAsync(tenantId, orderId, cancellationToken);
}
