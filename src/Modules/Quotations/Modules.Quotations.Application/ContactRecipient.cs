namespace Modules.Quotations.Application;

/// <summary>
/// El destinatario <c>Contact</c> de un envío (spec 2026-11-10 §4.6), resuelto <b>una sola vez</b> para la cotización y el pedido.
/// El contacto es un cliente más de la libreta, ligado al cliente del documento como su empresa: el backend lo comprueba contra el
/// puerto de Customers y nunca acepta un teléfono suelto.
/// </summary>
internal static class ContactRecipient
{
    /// <summary>Si el texto del borde pide al contacto. Por nombre, igual que el resto de los destinatarios: «2» no cuenta.</summary>
    public static bool IsRequested(string? recipient) =>
        !string.IsNullOrWhiteSpace(recipient)
        && string.Equals(recipient.Trim(), nameof(QuotationRecipient.Contact), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// El contacto de <paramref name="clientId"/>, o <c>{documento}.{documento}.contact_not_of_client</c> (422). Sin
    /// <paramref name="contactId"/> es el mismo código (G8): que falte equivale a «no es contacto de este cliente». El propio
    /// cliente del documento tampoco lo es. Se llama antes de cualquier efecto: un contacto que no corresponde es un envío que
    /// nunca empezó, y no deja historial, falla registrada ni elección de canal.
    /// </summary>
    public static async Task<QuotationClientContact> RequireAsync(
        IQuotationCustomerLookup customerLookup,
        Guid tenantId,
        Guid clientId,
        Guid? contactId,
        WhatsAppDocument document,
        CancellationToken cancellationToken)
    {
        var contact = contactId is { } id && id != clientId
            ? await customerLookup.FindContactAsync(tenantId, clientId, id, cancellationToken)
            : null;
        if (contact is not null)
        {
            return contact;
        }

        var prefix = WhatsAppDocuments.PrefixOf(document);
        throw new Modules.Quotations.Domain.QuotationsDomainException(
            $"{prefix}.{prefix}.contact_not_of_client",
            "The contact does not belong to the client of this document.");
    }

    /// <summary>
    /// La conexión del hilo, sólo si el destinatario es el contacto. El backend no puede saber si el contacto es «la persona del
    /// hilo»: esa regla es del frontend. Su propia guarda es ignorar <c>threadConnectionId</c> con cualquier otro destinatario.
    /// </summary>
    public static Guid? ThreadFor(bool isContact, Guid? threadConnectionId) => isContact ? threadConnectionId : null;
}
