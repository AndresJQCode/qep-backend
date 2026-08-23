namespace Modules.Pricing.Application;

/// <summary>
/// Si algún producto o algún cliente todavía referencia una lista de precios. Lo pregunta
/// <see cref="DeletePriceListHandler"/> antes de borrar, para que el caso normal salga como un
/// 422 que se entiende en vez de una violación de referencia cruzada convertida en 500.
///
/// **Es un puerto de `pricing`, no un tipo de `Catalog` ni de `Customers`.** `pricing` no puede
/// referenciar esos módulos —ninguno referencia al otro, `PricingLayerTests` lo verifica—, así
/// que la comprobación se pregunta al revés: el composition root cablea el adaptador contra los
/// repositorios que ya expone cada módulo. Mismo patrón que `ICustomerGeographyLookup` entre
/// `customers` y `geography` (CLI-01) y `IProductImageLookup` entre `catalog` y `storage`
/// (CAT-05).
/// </summary>
public interface IPriceListUsageLookup
{
    Task<bool> IsUsedByAnyProductAsync(
        Guid tenantId, Guid priceListId, CancellationToken cancellationToken);

    Task<bool> IsAssignedToAnyCustomerAsync(
        Guid tenantId, Guid priceListId, CancellationToken cancellationToken);
}
