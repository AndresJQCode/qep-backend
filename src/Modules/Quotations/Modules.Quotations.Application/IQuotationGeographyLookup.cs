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
}
