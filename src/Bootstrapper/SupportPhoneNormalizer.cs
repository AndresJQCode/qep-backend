using Modules.Customers.Domain;
using Modules.Support.Application;

namespace Bootstrapper;

/// <summary>
/// Los países y el celular del solicitante del formulario público (plan F19 y F20) sobre el normalizador de teléfonos de Customers,
/// igual que <see cref="StorePhoneNormalizer"/>: «un país real» es una región que conoce libphonenumber. Un solo adaptador para los
/// dos puertos, para que el país que se acepta y el E.164 que se arma no discrepen.
/// </summary>
internal sealed class SupportPhoneNormalizer(IPhoneNumberNormalizer phones) : ISupportCountries, ISupportPhoneNormalizer
{
    public bool IsKnown(string countryCode) => phones.IsKnownRegion(countryCode.Trim().ToUpperInvariant());

    public string? ToE164(string raw, string countryCode) => phones.ToE164(raw, countryCode.Trim().ToUpperInvariant());
}
