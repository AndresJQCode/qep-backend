using Modules.Customers.Domain;
using Modules.Hr.Application;

namespace Bootstrapper;

/// <summary>
/// <see cref="IHrPhoneNormalizer"/> sobre el normalizador de Customers, como <see cref="WholesalePhoneNormalizer"/>: el celular de un
/// empleado importado se guarda igual que el <c>phone_e164</c> de un cliente. Sólo traduce la firma: el país es Colombia, como el
/// del alta de un cliente sin país.
/// </summary>
internal sealed class HrPhoneNormalizer(IPhoneNumberNormalizer phones) : IHrPhoneNormalizer
{
    public string? ToE164(string phone) => phones.ToE164(phone, Customer.ColombiaCountryCode);
}
