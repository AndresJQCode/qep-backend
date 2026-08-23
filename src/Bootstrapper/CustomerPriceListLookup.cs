using Modules.Customers.Application;
using Modules.Pricing.Application;
using Modules.Pricing.Domain;

namespace Bootstrapper;

/// <summary>
/// Adapta el catálogo de listas de precio de `pricing` al puerto que `customers` declara.
///
/// **Vive acá y no en ninguno de los dos módulos**, mismo criterio que
/// <c>CustomerGeographyLookup</c> entre Customers y Geography (CLI-01) y
/// <c>CatalogPriceListLookup</c> entre Catalog y Pricing.
/// </summary>
internal sealed class CustomerPriceListLookup(IPriceListRepository priceListRepository)
    : ICustomerPriceListLookup
{
    public async Task<IReadOnlyDictionary<Guid, CustomerPriceListRef>> ListByIdsAsync(
        IReadOnlyCollection<Guid> priceListIds,
        CancellationToken cancellationToken)
    {
        var ids = priceListIds.Select(id => new PriceListId(id)).ToArray();
        var priceLists = await priceListRepository.ListByIdsAsync(ids, cancellationToken);

        return priceLists.ToDictionary(
            priceList => priceList.Id.Value,
            priceList => new CustomerPriceListRef(
                priceList.Id.Value,
                priceList.TenantId,
                priceList.Name,
                priceList.Prefix,
                priceList.IsActive));
    }
}
