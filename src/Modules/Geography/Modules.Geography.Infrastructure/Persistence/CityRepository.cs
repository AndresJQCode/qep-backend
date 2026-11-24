using Microsoft.EntityFrameworkCore;
using Modules.Geography.Application;
using Modules.Geography.Domain;

namespace Modules.Geography.Infrastructure.Persistence;

internal sealed class CityRepository(GeographyDbContext dbContext) : ICityRepository
{
    // Bogotá es el municipio 11001 del departamento 11 (Distrito Capital) y sigue ahí: no hay una segunda fila. Pero
    // geográficamente está en Cundinamarca (25), y quien arma una dirección la busca en esa lista. Por eso la lista de
    // Cundinamarca la incluye también (decisión del owner, 2026-11-23). Es una sola fila con un solo código, así que
    // el código DIVIPOLA único y la regla "empieza por el código de su departamento" se mantienen.
    private const string CundinamarcaDivipolaCode = "25";
    private const string BogotaDivipolaCode = "11001";

    public async Task<IReadOnlyList<City>> ListByDepartmentAsync(
        DepartmentId departmentId, CancellationToken cancellationToken) =>
        await CitiesOfDepartment(departmentId).ToArrayAsync(cancellationToken);

    private IQueryable<City> CitiesOfDepartment(DepartmentId departmentId)
    {
        // Subconsulta dentro de la misma sentencia: no es un viaje aparte a la base.
        return dbContext.Cities
            .AsNoTracking()
            .Where(city => city.DepartmentId == departmentId
                || (city.DivipolaCode == BogotaDivipolaCode
                    && dbContext.Departments.Any(department =>
                        department.Id == departmentId
                        && department.DivipolaCode == CundinamarcaDivipolaCode)));
    }

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
        DepartmentId departmentId, string name, CancellationToken cancellationToken) =>
        (await MatchByNameAsync(departmentId, name, cancellationToken))?.City;

    // Spec 2026-10-26 §4.1. Las ciudades primero, y en memoria por las razones de arriba; el sinónimo sólo si ninguna casa,
    // así que una fila de sinónimo con el nombre de una ciudad real (sólo puede llegar por SQL) nunca le gana. El sinónimo
    // sí va por SQL: normalized_text ya está normalizado al guardarse y el índice parcial lo encuentra.
    public async Task<CityNameMatch?> MatchByNameAsync(
        DepartmentId departmentId, string name, CancellationToken cancellationToken)
    {
        var target = NameMatching.Normalize(name);
        var cities = await dbContext.Cities
            .AsNoTracking()
            .Where(city => city.DepartmentId == departmentId)
            .ToArrayAsync(cancellationToken);
        var city = cities.SingleOrDefault(city => NameMatching.Normalize(city.Name) == target)
            ?? cities.SingleOrDefault(city => NameMatching.Normalize(city.DivipolaName) == target);
        if (city is not null)
        {
            return new CityNameMatch(city, SynonymId: null);
        }

        // A lo sumo una fila: IX_city_synonyms_department_text_active es único entre los no borrados.
        var match = await dbContext.CitySynonyms
            .AsNoTracking()
            .Where(synonym =>
                synonym.DepartmentId == departmentId &&
                synonym.NormalizedText == target &&
                synonym.DeletedAt == null)
            .Join(
                dbContext.Cities.AsNoTracking(),
                synonym => synonym.CityId,
                synonymCity => synonymCity.Id,
                (synonym, synonymCity) => new { synonym.Id, City = synonymCity })
            .SingleOrDefaultAsync(cancellationToken);
        return match is null ? null : new CityNameMatch(match.City, match.Id);
    }
}
