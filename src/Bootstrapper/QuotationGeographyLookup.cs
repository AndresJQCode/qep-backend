using Modules.Geography.Application;
using Modules.Geography.Domain;
using Modules.Quotations.Application;

namespace Bootstrapper;

/// <summary>
/// Adapta <c>ICityRepository</c> de Geography al puerto que <c>quotations</c> declara para
/// nombrar la ciudad de una parte con datos propios.
///
/// Vive acá y no en ninguno de los dos módulos, mismo criterio que <c>QuotationCompanyLookup</c>:
/// ningún módulo de negocio referencia al otro, y el composition root es el único lugar donde ese
/// acoplamiento es legítimo.
/// </summary>
internal sealed class QuotationGeographyLookup(ICityRepository cityRepository)
    : IQuotationGeographyLookup
{
    public async Task<IReadOnlyDictionary<Guid, string>> FindCityNamesAsync(
        IReadOnlyCollection<Guid> cityIds, CancellationToken cancellationToken)
    {
        var distinctIds = cityIds.Distinct().Select(id => new CityId(id)).ToArray();
        if (distinctIds.Length == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var cities = await cityRepository.ListByIdsAsync(distinctIds, cancellationToken);
        return cities.ToDictionary(city => city.Id.Value, city => city.Name);
    }
}
