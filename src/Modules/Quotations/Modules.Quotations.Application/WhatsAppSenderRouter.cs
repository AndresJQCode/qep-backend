using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// El Strategy de envío (spec 2026-10-21 D17, §4.3): según el proveedor del canal, la cotización sale por Zenvia o por la
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

    /// <summary>Los pedidos salen sólo por Zenvia (spec 2026-10-21 D12): <c>WhatsAppChannelSelection.ForOrder</c> descarta
    /// los canales de Meta antes de llegar aquí, así que uno de Meta es un error de programación, no del usuario.</summary>
    public Task SendOrderAsync(
        WhatsAppChannel channel, WhatsAppOrderMessage message, CancellationToken cancellationToken) =>
        channel is ZenviaWhatsAppChannel zenviaChannel
            ? zenvia.SendOrderAsync(zenviaChannel, message, cancellationToken)
            : throw new InvalidOperationException(
                $"Orders cannot be sent through the '{channel.Provider}' WhatsApp channel.");
}
