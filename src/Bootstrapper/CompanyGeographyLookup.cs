using Modules.Companies.Application;
using Modules.Geography.Application;
using Modules.Geography.Domain;

namespace Bootstrapper;

/// <summary>
/// Adapta los repositorios de <c>Geography</c> al puerto que <c>companies</c> declara.
///
/// **Vive acá y no en ninguno de los dos módulos**, mismo criterio que
/// <c>CustomerGeographyLookup</c> entre Customers y Geography: ningún módulo de negocio
/// referencia al otro, y el composition root —que ya referencia a los dos— es el único lugar
/// donde ese acoplamiento es legítimo.
///
/// **No decide nada.** Traduce ciudad + departamento de <c>Geography</c> al vocabulario de
/// <c>companies</c> y devuelve el dato crudo; la regla de que la ciudad exista antes de crear o
/// editar una empresa es de los handlers de <c>companies</c>.
/// </summary>
internal sealed class CompanyGeographyLookup(
    ICityRepository cityRepository,
    IDepartmentRepository departmentRepository) : ICompanyGeographyLookup
{
    public async Task<CompanyCityRef?> FindCityAsync(
        Guid cityId, CancellationToken cancellationToken)
    {
        var city = await cityRepository.FindAsync(new CityId(cityId), cancellationToken);
        if (city is null)
        {
            return null;
        }

        var department = await departmentRepository.FindAsync(
            city.DepartmentId, cancellationToken);
        return department is null ? null : ToRef(city, department);
    }

    public async Task<IReadOnlyDictionary<Guid, CompanyCityRef>> FindCitiesAsync(
        IReadOnlyCollection<Guid> cityIds, CancellationToken cancellationToken)
    {
        var distinctIds = cityIds.Distinct().Select(id => new CityId(id)).ToArray();
        var cities = await cityRepository.ListByIdsAsync(distinctIds, cancellationToken);
        if (cities.Count == 0)
        {
            return new Dictionary<Guid, CompanyCityRef>();
        }

        var departmentIds = cities.Select(city => city.DepartmentId).Distinct().ToArray();
        var departments = await departmentRepository.ListByIdsAsync(
            departmentIds, cancellationToken);
        var departmentsById = departments.ToDictionary(department => department.Id);

        var result = new Dictionary<Guid, CompanyCityRef>(cities.Count);
        foreach (var city in cities)
        {
            if (departmentsById.TryGetValue(city.DepartmentId, out var department))
            {
                result[city.Id.Value] = ToRef(city, department);
            }
        }

        return result;
    }

    private static CompanyCityRef ToRef(City city, Department department) =>
        new(
            city.Id.Value,
            city.DivipolaCode,
            city.Name,
            department.Id.Value,
            department.DivipolaCode,
            department.Name);
}
