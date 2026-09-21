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
}
