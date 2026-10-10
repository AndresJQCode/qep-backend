using Modules.Customers.Domain;
using PhoneNumbers;

namespace Modules.Customers.Infrastructure.Phones;

/// <summary>Spec 2026-10-09 §6.5 sobre <c>libphonenumber-csharp</c>. Con «+» la región se ignora; sin «+»
/// el número es nacional del país del cliente (la librería maneja los prefijos de larga distancia).</summary>
internal sealed class LibPhoneNumberNormalizer : IPhoneNumberNormalizer
{
    private static readonly PhoneNumberUtil Util = PhoneNumberUtil.GetInstance();

    public string? ToE164(string? phone, string country)
    {
        if (string.IsNullOrWhiteSpace(phone) || string.IsNullOrWhiteSpace(country))
        {
            return null;
        }

        try
        {
            var parsed = Util.Parse(phone.Trim(), country.Trim().ToUpperInvariant());
            return Util.IsValidNumber(parsed) ? Util.Format(parsed, PhoneNumberFormat.E164) : null;
        }
        catch (NumberParseException)
        {
            return null;
        }
    }
}
