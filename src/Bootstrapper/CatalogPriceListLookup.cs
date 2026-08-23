using Modules.Catalog.Application;
using Modules.Pricing.Application;
using Modules.Pricing.Domain;

namespace Bootstrapper;

/// <summary>
/// Adapta el catálogo de listas de precio de `pricing` al puerto que `catalog` declara.
///
/// **Vive acá y no en ninguno de los dos módulos**, mismo criterio que <c>ProductImageLookup</c>
/// entre Catalog y Storage (CAT-05) y <c>CustomerGeographyLookup</c> entre Customers y Geography:
/// ningún módulo de negocio referencia al otro, <c>CatalogLayerTests</c> lo verifica, y el
/// composition root —que ya referencia a los dos— es el único lugar donde ese acoplamiento es
/// legítimo.
///
/// **No decide nada.** Traduce las listas de precio de `pricing` al vocabulario de `catalog` y
/// devuelve el dato crudo, incluido `TenantId`: las reglas (que la lista exista en este tenant y
/// esté activa) son de <c>ProductPriceListResolver</c>.
/// </summary>
internal sealed class CatalogPriceListLookup(IPriceListRepository priceListRepository)
    : ICatalogPriceListLookup
{
    public async Task<IReadOnlyDictionary<Guid, CatalogPriceListRef>> ListByIdsAsync(
        IReadOnlyCollection<Guid> priceListIds,
        CancellationToken cancellationToken)
    {
        var ids = priceListIds.Select(id => new PriceListId(id)).ToArray();
        var priceLists = await priceListRepository.ListByIdsAsync(ids, cancellationToken);

        return priceLists.ToDictionary(
            priceList => priceList.Id.Value,
            priceList => new CatalogPriceListRef(
                priceList.Id.Value, priceList.TenantId, priceList.Name, priceList.IsActive));
    }
}
