using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>Spec 2026-10-30 §6.3.1: quién puede recibir qué categoría por la API externa.</summary>
public static class ExternalRecipientPolicy
{
    /// <summary>El código de error que frena el envío, o <c>null</c> si sale. <paramref name="contacts"/> son los clientes de ese número
    /// (<c>ListByPhoneAsync</c>) más el de la conversación, sin repetidos. Una conversación bloqueada frena toda categoría; el resto solo
    /// aplica a marketing. Con un solo cliente, inactivo gana sobre opt-out (como las campañas); con varios solo cuenta el opt-out; sin
    /// clientes sale.</summary>
    public static string? Evaluate(string category, bool blocked, IReadOnlyList<MessagingCustomerContact> contacts)
    {
        ArgumentNullException.ThrowIfNull(contacts);
        if (blocked)
        {
            return MessagingErrorCodes.ExternalRecipientBlocked;
        }

        if (!string.Equals(category, PreviewAudienceHandler.MarketingCategory, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (contacts.Count == 1)
        {
            var only = contacts[0];
            return !only.IsActive ? MessagingErrorCodes.ExternalRecipientInactive
                : only.MarketingOptOut ? MessagingErrorCodes.ExternalRecipientOptedOut
                : null;
        }

        return contacts.Any(contact => contact.MarketingOptOut) ? MessagingErrorCodes.ExternalRecipientOptedOut : null;
    }
}
