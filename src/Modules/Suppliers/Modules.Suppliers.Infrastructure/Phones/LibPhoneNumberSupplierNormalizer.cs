using Modules.Suppliers.Domain;
using PhoneNumbers;

namespace Modules.Suppliers.Infrastructure.Phones;

/// <summary>
/// <see cref="ISupplierPhoneNormalizer"/> sobre <c>libphonenumber-csharp</c>. Con «+» la región se
/// ignora; sin «+» el número es nacional del país del proveedor. Nunca lanza: un teléfono que no
/// parsea devuelve null y el proveedor se guarda igual.
/// </summary>
internal sealed class LibPhoneNumberSupplierNormalizer : ISupplierPhoneNormalizer
{
    private static readonly PhoneNumberUtil Util = PhoneNumberUtil.GetInstance();

    /// <summary>La región que la librería exige aunque el número traiga «+» (entonces la ignora).</summary>
    internal const string UnknownRegion = "ZZ";

    public string? ToE164(string? phone, string? country)
    {
        if (string.IsNullOrWhiteSpace(phone))
        {
            return null;
        }

        // Sin país, la región desconocida: un número con «+» igual parsea, uno nacional no.
        var region = string.IsNullOrWhiteSpace(country) ? UnknownRegion : country.Trim().ToUpperInvariant();

        try
        {
            var parsed = Util.Parse(phone.Trim(), region);
            return Util.IsValidNumber(parsed) ? Util.Format(parsed, PhoneNumberFormat.E164) : null;
        }
        catch (NumberParseException)
        {
            return null;
        }
    }
}
