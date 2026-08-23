namespace Modules.Customers.Application;

/// <summary>
/// Lo que `customers` necesita saber de una lista de precios del módulo `pricing` para validar y
/// nombrar las asignaciones de un cliente. **Es un puerto de `customers`, no un tipo de
/// `pricing`** — mismo criterio que <see cref="ICustomerGeographyLookup"/> hacia `Geography`
/// (CLI-01): el acoplamiento entre dos módulos de negocio vive en el composition root, no adentro
/// de un módulo. `customers` compila sin `pricing`, y
/// <c>CustomersLayerTests.ApplicationOnlyReferencesTenancyAmongTheBusinessModules</c> lo verifica.
/// </summary>
public interface ICustomerPriceListLookup
{
    /// <summary>
    /// Las listas de precio del lote, **sin filtrar por tenant**: el filtro es la garantía que
    /// este puerto existe para dar, y <see cref="CustomerPriceListResolver"/> es quien la aplica
    /// — mismo criterio que <see cref="ICustomerGeographyLookup.FindCitiesAsync"/>. Los ids que
    /// no existen simplemente no aparecen en el resultado.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, CustomerPriceListRef>> ListByIdsAsync(
        IReadOnlyCollection<Guid> priceListIds,
        CancellationToken cancellationToken);
}

/// <summary>
/// La proyección de una lista de precios que `customers` entiende. No es el agregado de
/// `pricing`: lleva sólo lo que hace falta para validar (tenant, activa) y para nombrarla en la
/// respuesta.
/// </summary>
public sealed record CustomerPriceListRef(Guid Id, Guid TenantId, string Name, string Prefix, bool IsActive);
