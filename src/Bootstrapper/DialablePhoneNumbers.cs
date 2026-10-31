using Modules.Customers.Domain;
using Modules.Messaging.Application;

namespace Bootstrapper;

/// <summary>
/// <see cref="IDialablePhoneNumbers"/> sobre el normalizador de Customers (decisión del owner 2026-10-30): un <c>wa_id</c> es un número
/// internacional, así que se le antepone «+» y la región se ignora. Sin indicativo real (<c>3001234567</c> se lee como +30, Grecia) no parsea.
/// </summary>
internal sealed class DialablePhoneNumbers(IPhoneNumberNormalizer phones) : IDialablePhoneNumbers
{
    private const string UnknownRegion = "ZZ";

    public bool IsDialable(string waId) => phones.ToE164("+" + waId, UnknownRegion) is not null;
}
