using Modules.Wholesale.Domain;

namespace Modules.Wholesale.Application;

/// <summary>
/// Las listas de precios del módulo. Toda lectura va acotada al tenant; <see cref="Add"/> y <see cref="Remove"/> no lo reciben
/// porque el agregado ya lo lleva.
/// </summary>
public interface IPriceListRepository
{
    /// <summary>La lista, o <c>null</c> si no es del tenant.</summary>
    Task<PriceList?> GetAsync(Guid tenantId, PriceListId id, CancellationToken cancellationToken);

    /// <summary>Todas las del tenant (sin paginar, D-12); el orden lo pone el handler.</summary>
    Task<IReadOnlyList<PriceList>> ListAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>En lote: los ids que no son del tenant no vienen.</summary>
    Task<IReadOnlyDictionary<PriceListId, PriceList>> FindManyAsync(
        Guid tenantId, IReadOnlyCollection<PriceListId> ids, CancellationToken cancellationToken);

    /// <summary>Si otra lista del tenant (distinta de <paramref name="except"/>) tiene ese nombre sin importar mayúsculas
    /// (D-12). El índice único de la base es la red de una carrera.</summary>
    Task<bool> NameExistsAsync(Guid tenantId, string name, PriceListId? except, CancellationToken cancellationToken);

    void Add(PriceList list);

    void Remove(PriceList list);
}

/// <summary>Cuántos distribuidores usan cada lista, en cualquier estado. Lo implementa el repositorio de distribuidores; una
/// lista sin distribuidores no viene.</summary>
public interface IDistributorUsage
{
    Task<IReadOnlyDictionary<PriceListId, int>> CountByPriceListAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>Una categoría del catálogo del mayorista con su nombre.</summary>
public sealed record WholesaleCategoryRef(Guid Id, string Name);

/// <summary>Un nodo del árbol plano de categorías del mayorista.</summary>
public sealed record WholesaleCategoryNode(Guid Id, string Name, Guid? ParentId);

/// <summary>Una variante vendible con su precio base en la moneda pedida y su nombre visible ya armado por el catálogo.</summary>
public sealed record WholesaleSampleVariant(Guid VariantId, string Code, string Name, decimal BasePrice);

/// <summary>El catálogo del mayorista visto desde Mayorista; adaptador en Bootstrapper sobre Catalog. Todo acotado al tenant.</summary>
public interface IWholesaleCatalogReader
{
    /// <summary>En lote: un id que no es una categoría del tenant (ajena o borrada) no viene.</summary>
    Task<IReadOnlyDictionary<Guid, WholesaleCategoryRef>> FindCategoriesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>Todas las categorías del tenant como árbol plano, ordenadas por nombre (ruling W5).</summary>
    Task<IReadOnlyList<WholesaleCategoryNode>> ListCategoriesAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>
    /// Hasta <paramref name="take"/> variantes vendibles con precio en <paramref name="currency"/>, ordenadas por nombre de
    /// producto y posición (D-11), y cuántos productos activos entran en el filtro. <paramref name="categoryIds"/> <c>null</c> es
    /// todo el catálogo activo; si no, esas categorías y sus descendientes (D-9).
    /// </summary>
    Task<(IReadOnlyList<WholesaleSampleVariant> Items, int TotalProducts)> SampleAsync(
        Guid tenantId,
        string currency,
        IReadOnlyCollection<Guid>? categoryIds,
        int take,
        CancellationToken cancellationToken);
}

/// <summary>Guarda los cambios del módulo con su auditoría y su outbox. Traduce los índices únicos por nombre a 409.</summary>
public interface IWholesaleUnitOfWork
{
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Auditoría como puerto propio (copia de <c>IPurchasesAuditPublisher</c>): el adaptador acumula la entrada en el contexto del
/// módulo para que se confirme o se revierta con el cambio de negocio (D-24).
/// </summary>
public interface IWholesaleAuditPublisher
{
    void Publish(
        Guid tenantId,
        Guid actorId,
        string action,
        string resourceType,
        string resourceId,
        string outcome,
        IReadOnlyCollection<string> changedFields,
        DateTimeOffset occurredAt);
}
