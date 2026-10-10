using Modules.Customers.Application;
using Modules.Messaging.Application;

namespace Bootstrapper;

/// <summary>§6.5: el wa_id de Meta son los dígitos del E.164; se traduce en las dos direcciones acá.</summary>
internal sealed class MessagingCustomerDirectory(ICustomerPhoneDirectory phones) : IMessagingCustomerDirectory
{
    /// <summary>El tope de <c>FindPhonesByNameAsync</c> (spec §6.5); el puerto de Customers no tiene default.</summary>
    public const int NameCap = 200;

    public async Task<IReadOnlyDictionary<string, CustomerRefDto>> MatchAsync(Guid tenantId, IReadOnlyCollection<string> waIds, CancellationToken cancellationToken)
    {
        var matches = await phones.MatchAsync(tenantId, waIds.Select(waId => "+" + waId).ToArray(), cancellationToken);
        return matches.ToDictionary(pair => pair.Key.TrimStart('+'), pair => new CustomerRefDto(pair.Value.Id, pair.Value.Name), StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<string>> FindWaIdsByNameAsync(Guid tenantId, string term, CancellationToken cancellationToken) =>
        (await phones.FindPhonesByNameAsync(tenantId, term, NameCap, cancellationToken)).Select(phone => phone.TrimStart('+')).ToArray();
}
