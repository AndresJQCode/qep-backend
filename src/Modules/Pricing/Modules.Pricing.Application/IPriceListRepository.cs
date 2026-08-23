using Modules.Pricing.Domain;

namespace Modules.Pricing.Application;

// Todo metodo recibe tenantId primero: el filtro de tenant es parte de la consulta, nunca un
// argumento opcional que el llamador se pueda olvidar.
//
// Sin busqueda por texto ni paginacion, mismo criterio que IClientClassificationRepository: las
// listas de precio de un tenant se cuentan con los dedos de una mano.
public interface IPriceListRepository
{
    Task<IReadOnlyList<PriceList>> ListAsync(
        Guid tenantId,
        CancellationToken cancellationToken);

    Task<PriceList?> FindAsync(
        Guid tenantId,
        PriceListId priceListId,
        CancellationToken cancellationToken);

    /// <summary>
    /// La version en lote de <see cref="FindAsync"/>, para que Catalog y Customers resuelvan de
    /// una sola consulta los ids de lista que referencian sus escalas/asignaciones, en vez de una
    /// consulta por id.
    /// </summary>
    Task<IReadOnlyList<PriceList>> ListByIdsAsync(
        Guid tenantId,
        IReadOnlyCollection<PriceListId> priceListIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// La misma consulta en lote, **sin filtrar por tenant**. Existe sólo para los adaptadores de
    /// Bootstrapper que implementan <c>ICatalogPriceListLookup</c>/<c>ICustomerPriceListLookup</c>
    /// (CAT-05/CLI-01, mismo criterio que <c>IProductImageLookup.FindManyAsync</c> hacia
    /// Storage): esos puertos no reciben tenantId porque el resolver del módulo consumidor es
    /// quien decide si el id es del tenant correcto, no este repositorio. Ningún caso de uso de
    /// `pricing` la llama.
    /// </summary>
    Task<IReadOnlyList<PriceList>> ListByIdsAsync(
        IReadOnlyCollection<PriceListId> priceListIds,
        CancellationToken cancellationToken);

    void Add(PriceList priceList);

    // Borrado real, no logico: desactivar ya existe y es la operacion que conserva historia. Este
    // es para la lista que se cargo por error y nunca se uso.
    void Remove(PriceList priceList);
}
