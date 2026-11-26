using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Siigo.Application.Invoicing;

/// <summary><c>POST …/orders/{orderId}/siigo-invoice/{invoiceId}/send-whatsapp</c> (<c>siigo.invoice.issue</c>).</summary>
public sealed record SendSiigoInvoiceWhatsAppCommand(Guid TenantId, Guid OrderId, Guid InvoiceId) : ICommand<SiigoInvoiceWhatsAppSent>;

/// <summary>El endpoint responde 204 y no lo mira; existe porque un comando devuelve un resultado.</summary>
public sealed record SiigoInvoiceWhatsAppSent(bool Sent);

/// <summary>
/// Spec 2026-11-21: manda el PDF de <b>una</b> factura al WhatsApp del cliente del pedido. Mismo permiso y misma doble capa que la descarga
/// (403, nunca 404); la fila tiene que ser de ese pedido y tenant y tener PDF, y si no es 404 sin llamar a Siigo ni al canal. El PDF se
/// pide a Siigo por el mismo camino que la descarga y viaja en memoria hasta el puerto: no se guarda. Canal, teléfono y plantilla son de
/// Quotations (<see cref="ISiigoInvoiceWhatsApp"/>), y sus errores <c>invoice.whatsapp.*</c> salen tal cual. Sólo un envío que salió se
/// audita, sin teléfono, URL ni contenido. No hay deduplicación: dos clics son dos mensajes y dos entradas de auditoría.
/// </summary>
public sealed class SendSiigoInvoiceWhatsAppHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ISiigoConnectionDirectory connections,
    ISiigoApi api,
    ISiigoInvoiceRepository invoices,
    ISiigoInvoiceWhatsApp whatsApp,
    ISiigoUnitOfWork unitOfWork,
    ISiigoAuditRecorder auditRecorder,
    IClock clock) : ICommandHandler<SendSiigoInvoiceWhatsAppCommand, SiigoInvoiceWhatsAppSent>
{
    public async Task<SiigoInvoiceWhatsAppSent> HandleAsync(SendSiigoInvoiceWhatsAppCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await SiigoInvoiceSupport.AuthorizeAsync(executionContext, tenantModules, command.TenantId, cancellationToken);

        var row = await SiigoInvoiceSupport.FindWithPdfAsync(invoices, command.TenantId, command.OrderId, command.InvoiceId, cancellationToken);
        var pdf = await SiigoInvoiceSupport.LoadPdfAsync(connections, api, row, cancellationToken);

        await whatsApp.SendAsync(command.TenantId, command.OrderId, row.Number ?? string.Empty, row.SiigoTotal, pdf, cancellationToken);

        SiigoInvoiceSupport.Audit(auditRecorder, executionContext, row, SiigoAuditActions.InvoiceWhatsAppSent, clock.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new SiigoInvoiceWhatsAppSent(true);
    }
}
