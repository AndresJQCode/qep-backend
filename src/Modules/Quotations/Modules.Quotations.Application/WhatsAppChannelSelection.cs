using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Por cuál canal sale el documento (plan 2026-10-21, Zenvia por tenant). Ninguno ⇒ <c>no_channel</c>; más de uno ⇒
/// <c>channel_required</c>: elegir entre varios es del plan 3 (spec 2026-10-21 D6), y mientras tanto no se adivina. Los
/// códigos del pedido son de pedido, como los demás de <see cref="SendOrderHandler"/>.
/// </summary>
internal static class WhatsAppChannelSelection
{
    // La cotización sale por cualquiera de los dos proveedores: hasta la elección explícita (Task 5) cuenta todo.
    public static WhatsAppChannel ForQuotation(IReadOnlyList<WhatsAppChannel> channels) =>
        Single(channels, "quotation");

    public static ZenviaWhatsAppChannel ForOrder(IReadOnlyList<WhatsAppChannel> channels)
    {
        // Los pedidos salen sólo por Zenvia (spec 2026-10-21 D12): una conexión de Meta no cuenta, ni para
        // `no_channel` ni para `channel_required`.
        var channel = Single([.. channels.OfType<ZenviaWhatsAppChannel>()], "order");

        // Antes que el teléfono y que el PDF: sin plantilla no sale ningún pedido, y saberlo no cuesta nada.
        if (string.IsNullOrWhiteSpace(channel.OrderTemplateId))
        {
            throw new QuotationsDomainException(
                "order.whatsapp.not_configured",
                "The tenant's WhatsApp channel has no order template: orders cannot be sent by WhatsApp yet.");
        }

        return channel;
    }

    private static T Single<T>(IReadOnlyList<T> channels, string prefix)
        where T : WhatsAppChannel =>
        channels.Count switch
        {
            0 => throw new QuotationsDomainException(
                $"{prefix}.whatsapp.no_channel",
                "The tenant has no WhatsApp connection available to send documents."),
            1 => channels[0],
            _ => throw new QuotationsDomainException(
                $"{prefix}.whatsapp.channel_required",
                "The tenant has more than one WhatsApp connection available: one has to be chosen."),
        };
}
