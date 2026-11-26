using Modules.Customers.Application;
using Modules.Quotations.Application;

namespace Bootstrapper;

/// <summary>
/// El adaptador de <see cref="IQuotationPhoneNumbers"/> (spec 2026-11-25 indicativo) sobre
/// <see cref="IPhoneNumberSplitter"/> de Customers. Sin región de respaldo: sólo se parte lo que trae su
/// país escrito, así que un número local nunca se reinterpreta como el de otro país. Nunca se pierden
/// dígitos: si quitar el indicativo se llevara algo más, responde que no se puede partir.
/// </summary>
internal sealed class QuotationPhoneNumbers(IPhoneNumberSplitter phones) : IQuotationPhoneNumbers
{
    /// <summary>La región de libphonenumber para los números no geográficos (<c>+800…</c>).</summary>
    private const string NonGeographicRegion = "001";

    public string? NationalNumberOf(string phone)
    {
        var parts = phones.Split(phone);

        // Un número no geográfico sin su indicativo no se puede marcar.
        if (parts is null || parts.Country == NonGeographicRegion)
        {
            return null;
        }

        // El partidor descarta lo que no es el número (una extensión, por ejemplo): sólo se parte si los
        // dígitos guardados son exactamente el indicativo más el número nacional.
        var digits = string.Concat(phone.Where(char.IsAsciiDigit));
        return digits == parts.CallingCode + parts.NationalNumber ? parts.NationalNumber : null;
    }
}
