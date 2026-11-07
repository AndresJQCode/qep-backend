using Modules.Customers.Domain;
using Modules.Store.Application;

namespace Bootstrapper;

/// <summary>
/// El puerto de países de la tienda sobre el normalizador de teléfonos de Customers, igual que
/// <see cref="ServiceContractsPhoneNormalizer"/>: el backend no tiene catálogo de países y «uno real» es una región que conoce
/// libphonenumber (hecho verificado 17). Es el único adaptador de Store sobre ese normalizador: el E.164 de la solicitud pública
/// se sumó acá en la Tarea 11, para que dos adaptadores no discrepen.
/// </summary>
internal sealed class StorePhoneNormalizer(IPhoneNumberNormalizer phones) : IStoreCountries, IStorePhoneNormalizer
{
    public bool IsKnown(string country) => phones.IsKnownRegion(country.Trim().ToUpperInvariant());

    public string? ToE164(string phone, string country) => phones.ToE164(phone, country.Trim().ToUpperInvariant());
}
