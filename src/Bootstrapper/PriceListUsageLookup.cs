using Modules.Catalog.Application;
using Modules.Customers.Application;
using Modules.Pricing.Application;

namespace Bootstrapper;

/// <summary>
/// Adapta los repositorios de `catalog` y `customers` al puerto que `pricing` declara para saber
/// si una lista de precios está en uso antes de borrarla.
///
/// **Vive acá y no en ninguno de los tres módulos.** Es el mismo criterio de siempre
/// (`ProductImageLookup`, `CustomerGeographyLookup`, `CatalogPriceListLookup`), con una vuelta:
/// acá el composition root cablea un puerto de `pricing` contra **dos** módulos distintos, porque
/// una lista puede estar "en uso" de dos maneras independientes — una escala de producto en
/// `catalog`, una asignación de cliente en `customers` — y ninguna de las dos referencia a
/// `pricing` ni a la otra.
/// </summary>
internal sealed class PriceListUsageLookup(
    IProductRepository productRepository,
    ICustomerPriceListRepository customerPriceListRepository) : IPriceListUsageLookup
{
    public Task<bool> IsUsedByAnyProductAsync(
        Guid tenantId, Guid priceListId, CancellationToken cancellationToken) =>
        productRepository.AnyWithPriceListAsync(tenantId, priceListId, cancellationToken);

    public Task<bool> IsAssignedToAnyCustomerAsync(
        Guid tenantId, Guid priceListId, CancellationToken cancellationToken) =>
        customerPriceListRepository.AnyWithPriceListAsync(tenantId, priceListId, cancellationToken);
}
