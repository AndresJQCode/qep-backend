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
internal sealed class QuotationGeographyLookup(
    ICityRepository cityRepository,
    IDepartmentRepository departmentRepository,
    IRegionRepository regionRepository)
    : IQuotationGeographyLookup
{
    public async Task<IReadOnlyDictionary<Guid, QuotationCityRef>> FindCitiesAsync(
        IReadOnlyCollection<Guid> cityIds, CancellationToken cancellationToken)
    {
        // Sin ids no se consulta: una cotización sin empresa no le cuesta una lectura a Geography.
        var distinctIds = cityIds.Distinct().Select(id => new CityId(id)).ToArray();
        if (distinctIds.Length == 0)
        {
            return new Dictionary<Guid, QuotationCityRef>();
        }

        var cities = await cityRepository.ListByIdsAsync(distinctIds, cancellationToken);
        if (cities.Count == 0)
        {
            return new Dictionary<Guid, QuotationCityRef>();
        }

        var departments = (await departmentRepository.ListByIdsAsync(
                cities.Select(city => city.DepartmentId).Distinct().ToArray(), cancellationToken))
            .ToDictionary(department => department.Id);

        // Mismo criterio que TaxRateGeographyLookup: una ciudad sin su departamento no sale.
        return cities
            .Where(city => departments.ContainsKey(city.DepartmentId))
            .ToDictionary(
                city => city.Id.Value,
                city => new QuotationCityRef(
                    city.Id.Value, city.Name, city.DepartmentId.Value, departments[city.DepartmentId].Name));
    }

    public async Task<IReadOnlyDictionary<Guid, string>> FindDepartmentNamesAsync(
        IReadOnlyCollection<Guid> departmentIds, CancellationToken cancellationToken)
    {
        var distinctIds = departmentIds.Distinct().Select(id => new DepartmentId(id)).ToArray();
        if (distinctIds.Length == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var departments = await departmentRepository.ListByIdsAsync(distinctIds, cancellationToken);
        return departments.ToDictionary(department => department.Id.Value, department => department.Name);
    }

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

    public async Task<IReadOnlyDictionary<Guid, string>> FindCoordinadoraCityNamesAsync(
        IReadOnlyCollection<Guid> cityIds, CancellationToken cancellationToken)
    {
        var distinctIds = cityIds.Distinct().Select(id => new CityId(id)).ToArray();
        if (distinctIds.Length == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var cities = await cityRepository.ListByIdsAsync(distinctIds, cancellationToken);
        return cities
            .Where(city => city.CoordinadoraName is not null)
            .ToDictionary(city => city.Id.Value, city => city.CoordinadoraName!);
    }

    public async Task<IReadOnlyDictionary<Guid, QuotationRegionRef>> FindRegionsAsync(
        IReadOnlyCollection<Guid> regionIds, CancellationToken cancellationToken)
    {
        // Sin ids no se toca el repositorio: un domicilio sin region es lo comun.
        var distinctIds = regionIds.Distinct().Select(id => new RegionId(id)).ToArray();
        if (distinctIds.Length == 0)
        {
            return new Dictionary<Guid, QuotationRegionRef>();
        }

        var regions = await regionRepository.ListByIdsAsync(distinctIds, cancellationToken);
        return regions.ToDictionary(
            region => region.Id.Value,
            region => new QuotationRegionRef(region.Id.Value, region.CountryCode, region.Name));
    }
}
