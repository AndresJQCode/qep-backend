using Modules.Customers.Domain;

namespace Modules.Customers.Application;

/// <summary>Spec 2026-10-10 §6.2: nombre y país de un cliente que nace de WhatsApp. Decisión del owner, en ese orden.</summary>
public static class IncompleteCustomerProfile
{
    public const string FallbackName = "Contacto de WhatsApp";

    public static string NameFor(WhatsAppContact contact)
    {
        ArgumentNullException.ThrowIfNull(contact);
        var name = FirstNonBlank(contact.ProfileName, contact.Username, contact.PhoneE164) ?? FallbackName;
        return name.Length <= Customer.NameMaxLength ? name : name[..Customer.NameMaxLength];
    }

    /// <summary>El prefijo ISO del BSUID («CO.…») si la librería conoce esa región; si no, la región del teléfono.</summary>
    public static string? CountryFor(WhatsAppContact contact, IPhoneNumberNormalizer normalizer)
    {
        ArgumentNullException.ThrowIfNull(contact);
        ArgumentNullException.ThrowIfNull(normalizer);
        if (contact.UserId.Length > 3 && contact.UserId[2] == '.')
        {
            var prefix = contact.UserId[..2].ToUpperInvariant();
            if (normalizer.IsKnownRegion(prefix))
            {
                return prefix;
            }
        }

        return normalizer.RegionOf(contact.PhoneE164);
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.Select(value => value?.Trim()).FirstOrDefault(value => !string.IsNullOrEmpty(value));
}
