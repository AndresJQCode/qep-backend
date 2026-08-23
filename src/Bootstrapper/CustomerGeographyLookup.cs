using Modules.Customers.Application;
using Modules.Geography.Application;
using Modules.Geography.Domain;

namespace Bootstrapper;

/// <summary>
/// Adapta los repositorios de <c>Geography</c> al puerto que <c>customers</c> declara.
///
/// **Vive aca y no en ninguno de los dos modulos**, mismo criterio que <c>ProductImageLookup</c>
/// entre Catalog y Storage (CAT-05): ningun modulo de negocio referencia al otro,
/// <c>CustomersLayerTests.ApplicationOnlyReferencesTenancyAmongTheBusinessModules</c> lo verifica,
/// y el composition root —que ya referencia a los dos— es el unico lugar donde ese acoplamiento es
/// legitimo.
///
/// **No decide nada.** Traduce ciudad + departamento de <c>Geography</c> al vocabulario de
/// <c>customers</c> y devuelve el dato crudo; las reglas (que la ciudad y la clasificacion existan
/// antes de armar el CUC) son de los handlers de <c>customers</c>.
/// </summary>
internal sealed class CustomerGeographyLookup(
    ICityRepository cityRepository,
    IDepartmentRepository departmentRepository) : ICustomerGeographyLookup
{
    public async Task<CustomerCityRef?> FindCityAsync(
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

    public async Task<IReadOnlyDictionary<Guid, CustomerCityRef>> FindCitiesAsync(
        IReadOnlyCollection<Guid> cityIds, CancellationToken cancellationToken)
    {
        var distinctIds = cityIds.Distinct().Select(id => new CityId(id)).ToArray();
        var cities = await cityRepository.ListByIdsAsync(distinctIds, cancellationToken);
        if (cities.Count == 0)
        {
            return new Dictionary<Guid, CustomerCityRef>();
        }

        var departmentIds = cities.Select(city => city.DepartmentId).Distinct().ToArray();
        var departments = await departmentRepository.ListByIdsAsync(
            departmentIds, cancellationToken);
        var departmentsById = departments.ToDictionary(department => department.Id);

        var result = new Dictionary<Guid, CustomerCityRef>(cities.Count);
        foreach (var city in cities)
        {
            if (departmentsById.TryGetValue(city.DepartmentId, out var department))
            {
                result[city.Id.Value] = ToRef(city, department);
            }
        }

        return result;
    }

    public async Task<CustomerCityRef?> FindCityByNameAsync(
        string departmentName, string cityName, CancellationToken cancellationToken)
    {
        // El departamento se resuelve primero y el resultado acota la busqueda de ciudad: el mismo
        // nombre de ciudad puede repetirse en mas de un departamento, asi que nunca se busca la
        // ciudad sola.
        var department = await departmentRepository.FindByNameAsync(departmentName, cancellationToken);
        if (department is null)
        {
            return null;
        }

        var city = await cityRepository.FindByNameAsync(department.Id, cityName, cancellationToken);
        return city is null ? null : ToRef(city, department);
    }

    public async Task<IReadOnlyList<CustomerDepartmentDto>> ListDepartmentsAsync(
        CancellationToken cancellationToken)
    {
        var departments = await departmentRepository.ListAllAsync(cancellationToken);
        return departments
            .Select(department =>
                new CustomerDepartmentDto(department.Id.Value, department.DivipolaCode, department.Name))
            .ToArray();
    }

    private static CustomerCityRef ToRef(City city, Department department) =>
        new(
            city.Id.Value,
            city.DivipolaCode,
            city.Name,
            department.Id.Value,
            department.DivipolaCode,
            department.Name);
}
