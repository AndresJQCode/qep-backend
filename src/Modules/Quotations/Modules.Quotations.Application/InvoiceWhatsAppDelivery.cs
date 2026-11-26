using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// La factura que Siigo ya emitió y se le manda al cliente del pedido. <paramref name="InvoiceTotal"/> es el total de la
/// factura; sin él se usa el del pedido. <paramref name="Pdf"/> son los bytes que Siigo entregó.
/// </summary>
public sealed record InvoiceWhatsAppRequest(
    Guid TenantId,
    Guid OrderId,
    string InvoiceNumber,
    decimal? InvoiceTotal,
    ReadOnlyMemory<byte> Pdf);

/// <summary>
/// La fachada de <c>quotations</c> para mandar una factura por WhatsApp: Siigo no conoce canales, plantillas ni teléfonos, y
/// este módulo no conoce a Siigo. Reusa las reglas del envío del pedido (<see cref="SendOrderHandler"/>) sin su historial
/// ni su auditoría: la factura la registra quien la emite.
/// </summary>
public interface IInvoiceWhatsAppDelivery
{
    /// <summary>
    /// Manda la factura. Los errores de dominio salen tal cual (<c>invoice.whatsapp.no_phone</c>, <c>no_channel</c>,
    /// <c>channel_required</c>, <c>send_failed</c>…). No cambia la elección de canal del tenant.
    /// </summary>
    Task SendAsync(InvoiceWhatsAppRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Si hoy se podría mandar: el cliente del pedido tiene teléfono y el canal se resuelve igual que en el envío (uno solo,
    /// o varios con la elección guardada del tenant).
    /// Que falte algo (pedido, teléfono, canal) responde <c>false</c>; una falla de infraestructura (base, directorio de canales) sí lanza.
    /// </summary>
    Task<bool> CanSendAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken);
}

/// <summary>
/// El orden de <see cref="SendAsync"/> deja todo lo que no cuesta antes de lo que sí: el pedido del tenant, el teléfono, quién
/// envía y el canal se resuelven antes de publicar el PDF o llamar a Zenvia.
///
/// Lo que no hace, a propósito: no llama a <c>Order.EnsureSendable()</c> (una factura existe aunque el pedido se anule
/// después; si Siigo ya lo impide no se repite la regla acá) y no recuerda la elección de canal (<c>Remember</c> se ignora:
/// mandar una factura no es elegir la conexión del tenant).
/// </summary>
public sealed class InvoiceWhatsAppDelivery(
    IOrderRepository orders,
    IQuotationCustomerLookup customerLookup,
    IWhatsAppChannelDirectory whatsAppChannels,
    IWhatsAppChannelChoiceStore channelChoices,
    ITenantDirectory tenantDirectory,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IWhatsAppSender whatsAppSender,
    IQuotationPdfStorage pdfStorage)
    : IInvoiceWhatsAppDelivery
{
    public async Task SendAsync(InvoiceWhatsAppRequest request, CancellationToken cancellationToken)
    {
        var order = await orders.FindUntrackedByIdAsync(
            request.TenantId, new OrderId(request.OrderId), cancellationToken)
            ?? throw OrderNotFound.ById(request.OrderId);

        // Sin teléfono, antes de cualquier trabajo de canal o de PDF: es la falla más probable y no cuesta nada saberla.
        var customer = await FindCustomerAsync(request.TenantId, order, cancellationToken)
            ?? throw new QuotationsDomainException(
                "order.order.client_not_found",
                $"Client '{order.ClientId}' was not found in this tenant.");
        // Con el mismo normalizador del sender: un texto sin dígitos no es un destinatario, y se sabe antes de publicar nada.
        if (WhatsAppPhone.Normalize(customer.Phone) is null)
        {
            throw new QuotationsDomainException(
                "invoice.whatsapp.no_phone",
                "The customer of the order has no phone to send the invoice to.");
        }

        var sentBy = await QuotationAdvisorResolver.ResolveAsync(
            membershipDirectory, executionContext, request.TenantId, cancellationToken);

        // La elección guardada del tenant, sin escribir una nueva: `Remember` se ignora a propósito.
        var channel = WhatsAppChannelSelection.Resolve(
            await whatsAppChannels.ListAvailableAsync(request.TenantId, WhatsAppDocument.Invoice, cancellationToken),
            await channelChoices.FindAsync(request.TenantId, cancellationToken),
            requested: null,
            WhatsAppDocument.Invoice).Channel;

        // Antes de publicar: si esta lectura falla no debe quedar una copia pública huérfana.
        var regional = await tenantDirectory.GetRegionalSettingsAsync(
            new TenantId(request.TenantId), cancellationToken);

        // Sólo Zenvia baja el documento por URL. Meta recibe los bytes y Mensajería los sube: no se publica nada.
        string? documentUrl = null;
        byte[]? document = null;
        var stage = "publish";
        try
        {
            if (channel is ZenviaWhatsAppChannel)
            {
                documentUrl = await pdfStorage.PublishInvoiceAsync(request.Pdf, cancellationToken);
            }
            else
            {
                document = request.Pdf.ToArray();
            }

            stage = "send";
            await whatsAppSender.SendInvoiceAsync(
                channel,
                new WhatsAppInvoiceMessage(
                    ToPhone: customer.Phone,
                    FullName: customer.Name,
                    InvoiceNumber: request.InvoiceNumber,
                    Total: request.InvoiceTotal ?? order.Total,
                    Currency: order.Currency,
                    DocumentUrl: documentUrl,
                    NumberFormat: regional?.NumberFormat,
                    Document: document,
                    SentByMemberId: sentBy.Value,
                    CustomerId: order.ClientId,
                    TenantId: request.TenantId),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not QuotationsDomainException and not OperationCanceledException)
        {
            // Un error de dominio ya se explica solo y sale tal cual. Lo demás (bucket público sin configurar, R2, un timeout de Zenvia o de
            // Meta) sale 422 con código propio y sólo el paso en el mensaje, como `order.send.failed`: ni la URL, ni el teléfono, ni el PDF.
            // La original viaja como InnerException para el log de fallas de la API.
            throw new QuotationsDomainException(
                "invoice.whatsapp.send_failed",
                $"The invoice could not be sent ({stage}).",
                exception);
        }
    }

    public async Task<bool> CanSendAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken)
    {
        var order = await orders.FindUntrackedByIdAsync(tenantId, new OrderId(orderId), cancellationToken);
        if (order is null)
        {
            return false;
        }

        var customer = await FindCustomerAsync(tenantId, order, cancellationToken);
        if (customer is null || WhatsAppPhone.Normalize(customer.Phone) is null)
        {
            return false;
        }

        // Misma resolución que el envío, con la elección guardada del tenant y sin guardar nada: dos canales sin elección
        // harían fallar el envío con `channel_required`, así que no se ofrece.
        var available = await whatsAppChannels.ListAvailableAsync(tenantId, WhatsAppDocument.Invoice, cancellationToken);
        try
        {
            WhatsAppChannelSelection.Resolve(
                available,
                await channelChoices.FindAsync(tenantId, cancellationToken),
                requested: null,
                WhatsAppDocument.Invoice);
            return true;
        }
        catch (QuotationsDomainException)
        {
            return false;
        }
    }

    // Que exista en este tenant: la ficha de otro tenant cuenta como no encontrada.
    private async Task<QuotationCustomerRef?> FindCustomerAsync(
        Guid tenantId, Order order, CancellationToken cancellationToken)
    {
        var customer = await customerLookup.FindAsync(tenantId, order.ClientId, cancellationToken);
        return customer is not null && customer.TenantId == tenantId ? customer : null;
    }
}
