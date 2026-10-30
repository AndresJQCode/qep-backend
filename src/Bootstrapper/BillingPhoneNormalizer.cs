using Modules.Billing.Application;
using Modules.Customers.Domain;

namespace Bootstrapper;

/// <summary>
/// <see cref="IBillingPhoneNormalizer"/> sobre el normalizador de Customers (ruling F5): el E.164 de un teléfono del Excel
/// tiene que salir idéntico al que Customers guarda en <c>phone_e164</c>, o el cliente creado por la importación no
/// se empareja con su WhatsApp. Sólo traduce la firma: sin país, Colombia (el contrato del puerto).
/// </summary>
internal sealed class BillingPhoneNormalizer(IPhoneNumberNormalizer phones) : IBillingPhoneNormalizer
{
    public string? ToE164(string? phone, string? country) =>
        phones.ToE164(phone, string.IsNullOrWhiteSpace(country) ? Customer.ColombiaCountryCode : country);

    public bool IsKnownCountry(string country) => phones.IsKnownRegion(country.Trim().ToUpperInvariant());
}
