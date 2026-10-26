using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>El canal por el que sale una cotización, y qué hacer con la elección (spec 2026-10-21 D5–D10).
/// <paramref name="Remember"/>: la persona eligió entre varios y esa elección se guarda con el envío.
/// <paramref name="PreviousChoice"/>: la fila que la nueva reemplaza (la vencida), o <c>null</c> si no había.</summary>
public sealed record ResolvedWhatsAppChannel(WhatsAppChannel Channel, bool Remember, Guid? PreviousChoice);

/// <summary>
/// Por cuál canal sale el documento (plan 2026-10-21, Zenvia por tenant). Ninguno ⇒ <c>no_channel</c>; los pedidos sólo
/// cuentan Zenvia y no eligen. Los códigos del pedido son de pedido, como los demás de <see cref="SendOrderHandler"/>.
/// </summary>
internal static class WhatsAppChannelSelection
{
    /// <summary>
    /// Para la cotización, que sale por cualquiera de los dos proveedores. En este orden (prefijo
    /// <c>quotation.whatsapp.</c>): sin canales, <c>no_channel</c>; con una elección guardada que sigue disponible se usa
    /// esa, y otro id es <c>channel_already_set</c>; con uno solo disponible se usa ese, y otro id es
    /// <c>channel_unavailable</c>; con varios hace falta el id (<c>channel_required</c>), y uno que no está es
    /// <c>channel_unavailable</c>. Una elección guardada que ya no está disponible cuenta como ninguna.
    /// </summary>
    public static ResolvedWhatsAppChannel Resolve(
        IReadOnlyList<WhatsAppChannel> available, Guid? saved, Guid? requested)
    {
        if (available.Count == 0)
        {
            throw NoChannel("quotation");
        }

        var chosen = available.FirstOrDefault(channel => channel.ConnectionId == saved);
        if (chosen is not null)
        {
            return requested is null || requested == chosen.ConnectionId
                ? new ResolvedWhatsAppChannel(chosen, Remember: false, PreviousChoice: null)
                : throw new QuotationsDomainException(
                    "quotation.whatsapp.channel_already_set",
                    "The tenant already chose its WhatsApp connection: it is changed from the settings, not when sending.");
        }

        if (available.Count == 1)
        {
            return requested is null || requested == available[0].ConnectionId
                ? new ResolvedWhatsAppChannel(available[0], Remember: false, PreviousChoice: null)
                : throw Unavailable();
        }

        if (requested is null)
        {
            throw new QuotationsDomainException(
                "quotation.whatsapp.channel_required",
                "The tenant has more than one WhatsApp connection available: one has to be chosen.");
        }

        var picked = available.FirstOrDefault(channel => channel.ConnectionId == requested) ?? throw Unavailable();
        return new ResolvedWhatsAppChannel(picked, Remember: true, PreviousChoice: saved);
    }

    private static QuotationsDomainException Unavailable() =>
        new("quotation.whatsapp.channel_unavailable", "The chosen WhatsApp connection is not available to send documents.");

    private static QuotationsDomainException NoChannel(string prefix) =>
        new($"{prefix}.whatsapp.no_channel", "The tenant has no WhatsApp connection available to send documents.");

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
            0 => throw NoChannel(prefix),
            1 => channels[0],
            _ => throw new QuotationsDomainException(
                $"{prefix}.whatsapp.channel_required",
                "The tenant has more than one WhatsApp connection available: one has to be chosen."),
        };
}
