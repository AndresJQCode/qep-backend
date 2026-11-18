using Modules.Customers.Domain;
using Modules.Wholesale.Application;

namespace Bootstrapper;

/// <summary>
/// <see cref="IWholesalePhoneNormalizer"/> sobre el normalizador de Customers, como <see cref="ServiceContractsPhoneNormalizer"/>:
/// el teléfono del aviso sale igual al <c>phone_e164</c> que guardaría un cliente, que es como Mensajería encuentra la
/// conversación. Sólo traduce la firma: sin país, Colombia.
/// </summary>
internal sealed class WholesalePhoneNormalizer(IPhoneNumberNormalizer phones) : IWholesalePhoneNormalizer
{
    public string? ToE164(string phone, string country) =>
        phones.ToE164(
            phone,
            string.IsNullOrWhiteSpace(country) ? Customer.ColombiaCountryCode : country.Trim().ToUpperInvariant());
}
