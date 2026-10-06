using Microsoft.EntityFrameworkCore;
using Modules.Geography.Application;
using Modules.Geography.Domain;

namespace Modules.Geography.Infrastructure.Persistence;

internal sealed class CityRepository(GeographyDbContext dbContext) : ICityRepository
{
    public async Task<IReadOnlyList<City>> ListByDepartmentAsync(
        DepartmentId departmentId, CancellationToken cancellationToken) =>
        await dbContext.Cities
            .AsNoTracking()
            .Where(city => city.DepartmentId == departmentId)
            .ToArrayAsync(cancellationToken);

    public Task<City?> FindAsync(CityId cityId, CancellationToken cancellationToken) =>
        dbContext.Cities
            .AsNoTracking()
            .SingleOrDefaultAsync(city => city.Id == cityId, cancellationToken);

    public async Task<IReadOnlyList<City>> ListByIdsAsync(
        IReadOnlyCollection<CityId> cityIds, CancellationToken cancellationToken) =>
        await dbContext.Cities
            .AsNoTracking()
            .Where(city => cityIds.Contains(city.Id))
            .ToArrayAsync(cancellationToken);

    public async Task<IReadOnlyList<City>> ListByDepartmentsAsync(
        IReadOnlyCollection<DepartmentId> departmentIds, CancellationToken cancellationToken) =>
        await dbContext.Cities
            .AsNoTracking()
            .Where(city => departmentIds.Contains(city.DepartmentId))
            .ToArrayAsync(cancellationToken);

    // En memoria y no ILike: ILike ya cubria mayusculas pero no tildes ("Bogota" vs "BOGOTÁ"), y
    // Postgres no tiene una funcion nativa de "sin tildes" sin la extension `unaccent`. Acotado por
    // departamento en la consulta (a lo sumo unas pocas decenas de ciudades), asi que traerlas
    // enteras y comparar con NameMatching.Normalize no pesa.
    //
    // Compara contra los dos nombres, el que se muestra y el oficial del DANE: quien llena el Excel
    // puede escribir "Cali" o "Santiago de Cali", y los dos son la misma ciudad. Sin nombre común
    // los dos son iguales, así que la ciudad sigue apareciendo una sola vez.
    public async Task<City?> FindByNameAsync(
        DepartmentId departmentId, string name, CancellationToken cancellationToken)
    {
        var target = NameMatching.Normalize(name);
        var cities = await dbContext.Cities
            .AsNoTracking()
            .Where(city => city.DepartmentId == departmentId)
            .ToArrayAsync(cancellationToken);
        return cities.SingleOrDefault(city =>
            NameMatching.Normalize(city.Name) == target ||
            NameMatching.Normalize(city.DivipolaName) == target);
    }
}
