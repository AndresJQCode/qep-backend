using Modules.Catalog.Application;
using Modules.Geography.Application;
using Modules.Geography.Domain;

namespace Bootstrapper;

/// <summary>
/// Adapta los repositorios de <c>Geography</c> al puerto que <c>catalog</c> declara para el municipio de una tasa
/// (spec ReteICA por empresa §7.2). Vive acá por la misma razón que <c>CompanyGeographyLookup</c>: ningún módulo de negocio
/// referencia al otro. No decide nada: devuelve lo que encuentra, y la regla (<c>catalog.tax_rate.city_invalid</c>) es del
/// handler.
/// </summary>
internal sealed class TaxRateGeographyLookup(
    ICityRepository cityRepository,
    IDepartmentRepository departmentRepository) : ITaxRateGeographyLookup
{
    public async Task<IReadOnlyDictionary<Guid, TaxRateCityRef>> FindCitiesAsync(
        IReadOnlyCollection<Guid> cityIds, CancellationToken cancellationToken)
    {
        // Sin ids no hay nada que buscar: una lista de tasas nacionales no le cuesta una consulta a Geography.
        if (cityIds.Count == 0)
        {
            return new Dictionary<Guid, TaxRateCityRef>();
        }

        var distinctIds = cityIds.Distinct().Select(id => new CityId(id)).ToArray();
        var cities = await cityRepository.ListByIdsAsync(distinctIds, cancellationToken);
        if (cities.Count == 0)
        {
            return new Dictionary<Guid, TaxRateCityRef>();
        }

        var departmentIds = cities.Select(city => city.DepartmentId).Distinct().ToArray();
        var departments = await departmentRepository.ListByIdsAsync(departmentIds, cancellationToken);
        var departmentsById = departments.ToDictionary(department => department.Id);

        var result = new Dictionary<Guid, TaxRateCityRef>(cities.Count);
        foreach (var city in cities)
        {
            if (departmentsById.TryGetValue(city.DepartmentId, out var department))
            {
                result[city.Id.Value] = new TaxRateCityRef(
                    city.Id.Value, city.Name, department.Id.Value, department.Name);
            }
        }

        return result;
    }
}
