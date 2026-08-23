using Modules.Customers.Domain;

namespace Modules.Customers.Application;

/// <summary>
/// Resuelve los <c>priceListId</c> que un <c>PUT /customers/{id}/price-lists</c> manda contra las
/// listas **del tenant del cliente**, y de paso trae nombre/prefijo para la respuesta — una sola
/// consulta al puerto, en vez de una por lista.
///
/// Existe por la misma razón que <c>ProductPriceListResolver</c> en Catalog: <c>PriceListId</c>
/// es referencia blanda, sin FK, así que esta es la única red debajo de la asignación.
/// </summary>
internal static class CustomerPriceListResolver
{
    public static async Task<IReadOnlyDictionary<Guid, CustomerPriceListRef>> ResolveAsync(
        ICustomerPriceListLookup lookup,
        Guid tenantId,
        IReadOnlyCollection<Guid> priceListIds,
        CancellationToken cancellationToken)
    {
        if (priceListIds.Count == 0)
        {
            return new Dictionary<Guid, CustomerPriceListRef>();
        }

        var found = await lookup.ListByIdsAsync(priceListIds, cancellationToken);

        foreach (var priceListId in priceListIds)
        {
            // Las dos condiciones dan el mismo código a propósito: distinguir "no existe" de "es
            // de otro tenant" le confirma al llamador que el id existe en otro tenant.
            if (!found.TryGetValue(priceListId, out var priceList) || priceList.TenantId != tenantId)
            {
                throw new CustomersDomainException(
                    "customers.customer.price_list_not_found",
                    "The price list was not found in this tenant.");
            }

            if (!priceList.IsActive)
            {
                throw new CustomersDomainException(
                    "customers.customer.price_list_inactive",
                    "An inactive price list cannot be assigned to a customer.");
            }
        }

        return found;
    }
}
