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

    public async Task<IReadOnlyList<CustomerDepartmentWithCitiesDto>> ListDepartmentsWithCitiesAsync(
        CancellationToken cancellationToken)
    {
        var departments = await departmentRepository.ListAllAsync(cancellationToken);

        // Una consulta por departamento (33 en total) en vez de traer las 1122 ciudades del pais
        // y agruparlas en memoria: este metodo solo lo llama la descarga de la plantilla de
        // importacion, no un endpoint de trafico alto, y ICityRepository ya expone
        // ListByDepartmentAsync para el caso de uso existente de FindCityByNameAsync.
        //
        // Secuencial y no Task.WhenAll: las consultas comparten el mismo DbContext con ambito de
        // request, y EF Core no admite mas de una operacion concurrente sobre la misma instancia
        // — en paralelo tira "A second operation was started on this context instance before a
        // previous operation completed", que la API mapea a 500.
        var result = new List<CustomerDepartmentWithCitiesDto>(departments.Count);
        foreach (var department in departments)
        {
            var cities = await cityRepository.ListByDepartmentAsync(department.Id, cancellationToken);
            result.Add(new CustomerDepartmentWithCitiesDto(
                department.Id.Value,
                department.DivipolaCode,
                department.Name,
                cities.Select(city => city.Name).ToArray()));
        }

        return result;
    }

    public async Task<IReadOnlyCollection<Guid>> ListCityIdsByDepartmentsAsync(
        IReadOnlyCollection<Guid> departmentIds, CancellationToken cancellationToken)
    {
        if (departmentIds.Count == 0)
        {
            return [];
        }

        var ids = departmentIds.Select(id => new DepartmentId(id)).ToArray();
        var cities = await cityRepository.ListByDepartmentsAsync(ids, cancellationToken);
        return cities.Select(city => city.Id.Value).ToArray();
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
