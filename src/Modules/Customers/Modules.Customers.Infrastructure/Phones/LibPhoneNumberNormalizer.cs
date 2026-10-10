using Modules.Customers.Domain;
using PhoneNumbers;

namespace Modules.Customers.Infrastructure.Phones;

/// <summary>Spec 2026-10-09 §6.5 sobre <c>libphonenumber-csharp</c>. Con «+» la región se ignora; sin «+»
/// el número es nacional del país del cliente (la librería maneja los prefijos de larga distancia).</summary>
internal sealed class LibPhoneNumberNormalizer : IPhoneNumberNormalizer
{
    /// <summary>La región que la librería usa cuando el número trae «+»: la ignora, pero exige una.</summary>
    internal const string UnknownRegion = "ZZ";

    private static readonly PhoneNumberUtil Util = PhoneNumberUtil.GetInstance();

    // GetSupportedRegions es un HashSet que la librería arma una vez; se copia para no depender de su mutabilidad.
    private static readonly HashSet<string> SupportedRegions = new(Util.GetSupportedRegions(), StringComparer.Ordinal);

    public bool IsKnownRegion(string regionCode) =>
        !string.IsNullOrWhiteSpace(regionCode) && SupportedRegions.Contains(regionCode.Trim().ToUpperInvariant());

    public string? RegionOf(string? e164)
    {
        if (string.IsNullOrWhiteSpace(e164))
        {
            return null;
        }

        try
        {
            var region = Util.GetRegionCodeForNumber(Util.Parse(e164.Trim(), UnknownRegion));
            return string.IsNullOrEmpty(region) || region == UnknownRegion ? null : region;
        }
        catch (NumberParseException)
        {
            return null;
        }
    }

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
