using Modules.Geography.Application;
using Modules.Geography.Domain;
using Modules.Suppliers.Application;

namespace Bootstrapper;

/// <summary>
/// Adapta los repositorios de <c>Geography</c> al puerto que <c>suppliers</c> declara.
///
/// **Vive acá y no en ninguno de los dos módulos**, mismo criterio que
/// <c>CustomerGeographyLookup</c> entre Customers y Geography: ningún módulo de negocio
/// referencia al otro, y el composition root —que ya referencia a los dos— es el único lugar
/// donde ese acoplamiento es legítimo.
///
/// **No decide nada.** Traduce ciudad + departamento de <c>Geography</c> al vocabulario de
/// <c>suppliers</c> y devuelve el dato crudo; la regla de que la ciudad exista antes de crear o
/// editar un proveedor es de los handlers de <c>suppliers</c>.
/// </summary>
internal sealed class SupplierGeographyLookup(
    ICityRepository cityRepository,
    IDepartmentRepository departmentRepository) : ISupplierGeographyLookup
{
    public async Task<SupplierCityRef?> FindCityAsync(
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

    public async Task<IReadOnlyDictionary<Guid, SupplierCityRef>> FindCitiesAsync(
        IReadOnlyCollection<Guid> cityIds, CancellationToken cancellationToken)
    {
        // Sin ids no hay nada que buscar: mismo atajo que CustomerGeographyLookup.
        if (cityIds.Count == 0)
        {
            return new Dictionary<Guid, SupplierCityRef>();
        }

        var distinctIds = cityIds.Distinct().Select(id => new CityId(id)).ToArray();
        var cities = await cityRepository.ListByIdsAsync(distinctIds, cancellationToken);
        if (cities.Count == 0)
        {
            return new Dictionary<Guid, SupplierCityRef>();
        }

        var departmentIds = cities.Select(city => city.DepartmentId).Distinct().ToArray();
        var departments = await departmentRepository.ListByIdsAsync(
            departmentIds, cancellationToken);
        var departmentsById = departments.ToDictionary(department => department.Id);

        var result = new Dictionary<Guid, SupplierCityRef>(cities.Count);
        foreach (var city in cities)
        {
            if (departmentsById.TryGetValue(city.DepartmentId, out var department))
            {
                result[city.Id.Value] = ToRef(city, department);
            }
        }

        return result;
    }

    private static SupplierCityRef ToRef(City city, Department department) =>
        new(
            city.Id.Value,
            city.DivipolaCode,
            city.Name,
            department.Id.Value,
            department.DivipolaCode,
            department.Name);
}
