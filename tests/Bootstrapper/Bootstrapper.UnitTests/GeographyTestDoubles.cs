using Modules.Geography.Application;
using Modules.Geography.Domain;

namespace Bootstrapper.UnitTests;

// Dobles de los repositorios de Geography que usan los adaptadores del composition root. A mano y sin
// librería de mocking, como el resto del repositorio. Cuentan cada llamada: lo que se prueba con ellos
// es cuántas veces un adaptador va a la base, no qué devuelve.

internal sealed class CountingCityRepository(params City[] cities) : ICityRepository
{
    public int Calls { get; private set; }

    public Task<IReadOnlyList<City>> ListByIdsAsync(
        IReadOnlyCollection<CityId> cityIds, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult<IReadOnlyList<City>>(
            cities.Where(city => cityIds.Contains(city.Id)).ToList());
    }

    public Task<IReadOnlyList<City>> ListByDepartmentAsync(
        DepartmentId departmentId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<City?> FindAsync(CityId cityId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<City?> FindByNameAsync(
        DepartmentId departmentId, string name, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<City>> ListByDepartmentsAsync(
        IReadOnlyCollection<DepartmentId> departmentIds, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

internal sealed class CountingDepartmentRepository(params Department[] departments) : IDepartmentRepository
{
    public int Calls { get; private set; }

    public Task<IReadOnlyList<Department>> ListByIdsAsync(
        IReadOnlyCollection<DepartmentId> departmentIds, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult<IReadOnlyList<Department>>(
            departments.Where(department => departmentIds.Contains(department.Id)).ToList());
    }

    public Task<IReadOnlyList<Department>> ListAllAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<Department?> FindAsync(DepartmentId departmentId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<Department?> FindByNameAsync(string name, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}
