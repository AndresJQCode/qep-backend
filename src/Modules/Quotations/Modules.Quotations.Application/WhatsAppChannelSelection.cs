using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>El canal por el que sale un documento, y qué hacer con la elección (spec 2026-10-21 D5–D10).
/// <paramref name="Remember"/>: la persona eligió entre varios y esa elección se guarda con el envío.
/// <paramref name="PreviousChoice"/>: la fila que la nueva reemplaza (la vencida), o <c>null</c> si no había.</summary>
public sealed record ResolvedWhatsAppChannel(WhatsAppChannel Channel, bool Remember, Guid? PreviousChoice);

/// <summary>
/// Por cuál canal sale el documento (plan 2026-10-21, Zenvia por tenant; spec 2026-11-09 D7: el pedido con las mismas reglas que la
/// cotización). La lista ya viene filtrada por documento (D6); acá sólo se eligen y se nombran los códigos con el prefijo del documento.
/// </summary>
internal static class WhatsAppChannelSelection
{
    /// <summary>
    /// En este orden (prefijo <c>{document}.whatsapp.</c>): sin canales, <c>no_channel</c>; con una elección guardada que sigue
    /// disponible se usa esa, y otro id es <c>channel_already_set</c>; con uno solo disponible se usa ese, y otro id es
    /// <c>channel_unavailable</c>; con varios hace falta el id (<c>channel_required</c>), y uno que no está es
    /// <c>channel_unavailable</c>. Una elección guardada que ya no está disponible para este documento cuenta como ninguna.
    /// <paramref name="thread"/> (spec 2026-11-10 A12): la conexión del hilo desde el que se manda. Si está entre las disponibles
    /// gana sobre todo lo demás y <b>no se recuerda</b>: no es la elección del tenant. Si no está, la resolución es la de siempre.
    /// </summary>
    public static ResolvedWhatsAppChannel Resolve(
        IReadOnlyList<WhatsAppChannel> available,
        Guid? saved,
        Guid? requested,
        WhatsAppDocument document = WhatsAppDocument.Quotation,
        Guid? thread = null)
    {
        var prefix = WhatsAppDocuments.PrefixOf(document);
        if (available.Count == 0)
        {
            throw new QuotationsDomainException(
                $"{prefix}.whatsapp.no_channel", "The tenant has no WhatsApp connection available to send documents.");
        }

        var viaThread = thread is null ? null : available.FirstOrDefault(channel => channel.ConnectionId == thread);
        if (viaThread is not null)
        {
            return new ResolvedWhatsAppChannel(viaThread, Remember: false, PreviousChoice: null);
        }

        var chosen = available.FirstOrDefault(channel => channel.ConnectionId == saved);
        if (chosen is not null)
        {
            return requested is null || requested == chosen.ConnectionId
                ? new ResolvedWhatsAppChannel(chosen, Remember: false, PreviousChoice: null)
                : throw new QuotationsDomainException(
                    $"{prefix}.whatsapp.channel_already_set",
                    "The tenant already chose its WhatsApp connection: it is changed from the settings, not when sending.");
        }

        if (available.Count == 1)
        {
            return requested is null || requested == available[0].ConnectionId
                ? new ResolvedWhatsAppChannel(available[0], Remember: false, PreviousChoice: null)
                : throw Unavailable(prefix);
        }

        if (requested is null)
        {
            throw new QuotationsDomainException(
                $"{prefix}.whatsapp.channel_required",
                "The tenant has more than one WhatsApp connection available: one has to be chosen.");
        }

        var picked = available.FirstOrDefault(channel => channel.ConnectionId == requested) ?? throw Unavailable(prefix);
        return new ResolvedWhatsAppChannel(picked, Remember: true, PreviousChoice: saved);
    }

    private static QuotationsDomainException Unavailable(string prefix) =>
        new($"{prefix}.whatsapp.channel_unavailable", "The chosen WhatsApp connection is not available to send documents.");
}
