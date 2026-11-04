namespace Modules.Store.Application;

/// <summary>
/// Lo que la tienda lee del catálogo. Esta tarea trae la parte de ajustes; las lecturas públicas (productos, árbol de categorías)
/// llegan con la Tarea 10 sobre este mismo puerto.
/// </summary>
public interface IStoreCatalogReader
{
    /// <summary>Nombre de cada categoría del tenant que exista; un id borrado o ajeno simplemente no aparece.</summary>
    Task<IReadOnlyDictionary<Guid, string>> CategoryNamesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>Las monedas con al menos un precio en el tenant, la fuente de <c>GET /catalog/currencies-in-use</c>.</summary>
    Task<IReadOnlyCollection<string>> CurrenciesInUseAsync(Guid tenantId, CancellationToken cancellationToken);
}
