using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// El Strategy de envío (spec 2026-10-21 D17, §4.3): según el proveedor del canal, el documento sale por Zenvia o por la
/// conexión de Meta del tenant. Es scoped porque el puerto de Meta cruza a Mensajería, que usa el <c>DbContext</c> del request.
///
/// El teléfono se valida aquí, antes de llamar a cualquiera de los dos: sin teléfono el envío nunca empezó, y el código es
/// el mismo sea cual sea el proveedor.
/// </summary>
public sealed class WhatsAppSenderRouter(IZenviaWhatsAppSender zenvia, IMetaWhatsAppSender meta) : IWhatsAppSender
{
    public Task SendQuotationAsync(
        WhatsAppChannel channel, WhatsAppQuotationMessage message, CancellationToken cancellationToken)
    {
        var waId = WhatsAppPhone.Normalize(message.ToPhone)
            ?? throw new QuotationsDomainException(
                "quotation.whatsapp.recipient_missing",
                "The client has no phone number to send the quotation to.");

        return channel switch
        {
            ZenviaWhatsAppChannel zenviaChannel => zenvia.SendQuotationAsync(zenviaChannel, message, cancellationToken),
            MetaWhatsAppChannel metaChannel => meta.SendQuotationAsync(metaChannel, waId, message, cancellationToken),
            _ => throw new InvalidOperationException($"Unknown WhatsApp channel provider '{channel.Provider}'."),
        };
    }

    /// <summary>Spec 2026-11-09 D9: el pedido sale por los dos proveedores, como la cotización. El teléfono se normaliza antes de
    /// bifurcar; Zenvia vuelve a normalizarlo, y da igual.</summary>
    public Task SendOrderAsync(
        WhatsAppChannel channel, WhatsAppOrderMessage message, CancellationToken cancellationToken)
    {
        var waId = WhatsAppPhone.Normalize(message.ToPhone)
            ?? throw new QuotationsDomainException(
                "order.whatsapp.recipient_missing",
                "The recipient has no phone number to send the order to.");

        return channel switch
        {
            ZenviaWhatsAppChannel zenviaChannel => zenvia.SendOrderAsync(zenviaChannel, message, cancellationToken),
            MetaWhatsAppChannel metaChannel => meta.SendOrderAsync(metaChannel, waId, message, cancellationToken),
            _ => throw new InvalidOperationException($"Unknown WhatsApp channel provider '{channel.Provider}'."),
        };
    }
}
