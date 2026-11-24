namespace Modules.Quotations.Application;

/// <summary>
/// Puerto hacia Geography para poner nombre a la ciudad de una parte (<see cref="QuotationParty"/>)
/// — factura o entrega con datos propios. Mismo criterio de aislamiento que
/// <see cref="IQuotationCustomerLookup"/>: el adaptador vive en <c>Bootstrapper</c>.
///
/// Separado de <see cref="ICustomerGeographyLookup"/> (que resuelve la libreta del cliente) porque
/// resuelve algo distinto: el <c>CityId</c> que vive en <see cref="QuotationParty"/> misma, no en
/// una dirección de <c>Customer</c>.
/// </summary>
public interface IQuotationGeographyLookup
{
    Task<IReadOnlyDictionary<Guid, string>> FindCityNamesAsync(
        IReadOnlyCollection<Guid> cityIds, CancellationToken cancellationToken);

    /// <summary>
    /// El nombre de cada ciudad como lo escribe Coordinadora ("ABEJORRAL (ANT)"), para la columna
    /// "Ciudad Coordinadora" del Excel de pedidos (ajuste 2026-10-02). Sólo trae las ciudades que
    /// lo tienen: una que Coordinadora no lista, o un id que no existe, no aparece en el
    /// diccionario. No hay respaldo al nombre del DANE a propósito, porque la transportadora no lo
    /// reconoce.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, string>> FindCoordinadoraCityNamesAsync(
        IReadOnlyCollection<Guid> cityIds, CancellationToken cancellationToken);

    /// <summary>
    /// El nombre de cada departamento, para la columna «Departamento facturación» del Excel de pedidos
    /// (ajuste 2026-10-28) cuando se factura a una parte con datos propios. Un id que no existe no
    /// aparece en el diccionario, mismo criterio que <see cref="FindCityNamesAsync"/>.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, string>> FindDepartmentNamesAsync(
        IReadOnlyCollection<Guid> departmentIds, CancellationToken cancellationToken);

    /// <summary>
    /// La referencia de municipio de cada ciudad (spec ReteICA por empresa §9.1, plan P11): nombre y departamento, para el
    /// municipio de la operación y el de la empresa. Un id que no existe no aparece en el diccionario; también es la
    /// prueba de que un <c>operationCityId</c> explícito existe.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, QuotationCityRef>> FindCitiesAsync(
        IReadOnlyCollection<Guid> cityIds, CancellationToken cancellationToken);

    /// <summary>
    /// Resuelve regiones (provincias) por id, en lote y con una sola consulta: pais al que
    /// pertenecen y nombre. Los ids que no existen no salen. Sin ids no se consulta.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, QuotationRegionRef>> FindRegionsAsync(
        IReadOnlyCollection<Guid> regionIds, CancellationToken cancellationToken);
}

/// <summary>Un municipio con su departamento, la forma de toda referencia de municipio del spec (§9.1).</summary>
public sealed record QuotationCityRef(Guid Id, string Name, Guid DepartmentId, string DepartmentName);

/// <summary>Una region de <c>Geography</c> en el vocabulario del modulo.</summary>
public sealed record QuotationRegionRef(Guid Id, string CountryCode, string Name);
