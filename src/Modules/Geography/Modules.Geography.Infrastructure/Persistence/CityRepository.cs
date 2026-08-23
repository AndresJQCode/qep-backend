using Microsoft.EntityFrameworkCore;
using Modules.Geography.Application;
using Modules.Geography.Domain;

namespace Modules.Geography.Infrastructure.Persistence;

internal sealed class CityRepository(GeographyDbContext dbContext) : ICityRepository
{
    private const string LikeEscapeCharacter = "\\";

    private static string EscapeLikeWildcards(string term) => term
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);


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

    // ILike y no ToLower(): evita los analizadores de sensibilidad cultural (CA1304/CA1311/
    // CA1862) y es la comparacion case-insensitive nativa de Npgsql.
    public Task<City?> FindByNameAsync(
        DepartmentId departmentId, string name, CancellationToken cancellationToken)
    {
        var pattern = EscapeLikeWildcards(name.Trim());
        return dbContext.Cities
            .AsNoTracking()
            .SingleOrDefaultAsync(
                city =>
                    city.DepartmentId == departmentId &&
                    EF.Functions.ILike(city.Name, pattern, LikeEscapeCharacter),
                cancellationToken);
    }
}
