namespace Modules.Catalog.Application;

/// <summary>
/// Lo que `catalog` necesita saber de una lista de precios del módulo `pricing` para validar y
/// nombrar las escalas de un producto. **Es un puerto de `catalog`, no un tipo de `pricing`** —
/// mismo criterio que <see cref="IProductImageLookup"/> hacia `Storage` (CAT-05): el acoplamiento
/// entre dos módulos de negocio vive en el composition root, no adentro de un módulo. `catalog`
/// compila sin `pricing`, y `CatalogLayerTests` lo verifica.
/// </summary>
public interface ICatalogPriceListLookup
{
    /// <summary>
    /// Las listas de precio del lote, **sin filtrar por tenant**: el filtro es la garantía que
    /// este puerto existe para dar, y <see cref="ProductPriceListResolver"/> es quien la aplica —
    /// mismo criterio que <see cref="IProductImageLookup.FindAsync"/>. Los ids que no existen
    /// simplemente no aparecen en el resultado.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, CatalogPriceListRef>> ListByIdsAsync(
        IReadOnlyCollection<Guid> priceListIds,
        CancellationToken cancellationToken);
}

/// <summary>
/// La proyección de una lista de precios que `catalog` entiende. No es el agregado de `pricing`:
/// lleva sólo lo que hace falta para validar (tenant, activa) y para nombrar la escala en la
/// respuesta.
/// </summary>
public sealed record CatalogPriceListRef(Guid Id, Guid TenantId, string Name, bool IsActive);
