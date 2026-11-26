using Modules.Geography.Application;
using Modules.Geography.Domain;
using Modules.Hr.Application;

namespace Bootstrapper;

/// <summary>
/// Adapta los repositorios de Geography al puerto de Hr (calco de <see cref="CompanyGeographyLookup"/>). No decide nada: traduce
/// al vocabulario de Hr y devuelve el dato crudo; que la ciudad o el país existan lo decide Application.
/// </summary>
internal sealed class HrGeographyLookup(
    ICityRepository cityRepository,
    IDepartmentRepository departmentRepository,
    ICountryRepository countryRepository) : IHrGeographyLookup
{
    public async Task<IReadOnlyDictionary<Guid, HrCityRef>> FindCitiesAsync(
        IReadOnlyCollection<Guid> cityIds, CancellationToken cancellationToken)
    {
        if (cityIds.Count == 0)
        {
            return new Dictionary<Guid, HrCityRef>();
        }

        var cities = await cityRepository.ListByIdsAsync(
            cityIds.Distinct().Select(id => new CityId(id)).ToArray(), cancellationToken);
        return await ToRefsAsync(cities, cancellationToken);
    }

    public async Task<IReadOnlyList<HrCityRef>> ListAllCitiesAsync(CancellationToken cancellationToken)
    {
        var departments = await departmentRepository.ListAllAsync(cancellationToken);
        var cities = await cityRepository.ListByDepartmentsAsync(
            departments.Select(department => department.Id).ToArray(), cancellationToken);
        return (await ToRefsAsync(cities, cancellationToken)).Values.ToList();
    }

    public async Task<IReadOnlyList<HrCountryRef>> ListCountriesAsync(CancellationToken cancellationToken) =>
        (await countryRepository.ListAllAsync(cancellationToken))
            .Select(country => new HrCountryRef(country.Code, country.Name))
            .ToList();

    private async Task<IReadOnlyDictionary<Guid, HrCityRef>> ToRefsAsync(
        IReadOnlyList<City> cities, CancellationToken cancellationToken)
    {
        if (cities.Count == 0)
        {
            return new Dictionary<Guid, HrCityRef>();
        }

        var departments = (await departmentRepository.ListByIdsAsync(
                cities.Select(city => city.DepartmentId).Distinct().ToArray(), cancellationToken))
            .ToDictionary(department => department.Id);

        var result = new Dictionary<Guid, HrCityRef>(cities.Count);
        foreach (var city in cities)
        {
            if (departments.TryGetValue(city.DepartmentId, out var department))
            {
                result[city.Id.Value] = new HrCityRef(
                    city.Id.Value, city.Name, city.DivipolaName, department.Id.Value, department.Name);
            }
        }

        return result;
    }
}
