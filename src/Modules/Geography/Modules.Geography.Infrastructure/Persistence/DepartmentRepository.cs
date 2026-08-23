using Microsoft.EntityFrameworkCore;
using Modules.Geography.Application;
using Modules.Geography.Domain;

namespace Modules.Geography.Infrastructure.Persistence;

internal sealed class DepartmentRepository(GeographyDbContext dbContext) : IDepartmentRepository
{
    private const string LikeEscapeCharacter = "\\";

    private static string EscapeLikeWildcards(string term) => term
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);


    public async Task<IReadOnlyList<Department>> ListAllAsync(CancellationToken cancellationToken) =>
        await dbContext.Departments
            .AsNoTracking()
            .ToArrayAsync(cancellationToken);

    public Task<Department?> FindAsync(
        DepartmentId departmentId, CancellationToken cancellationToken) =>
        dbContext.Departments
            .AsNoTracking()
            .SingleOrDefaultAsync(department => department.Id == departmentId, cancellationToken);

    public async Task<IReadOnlyList<Department>> ListByIdsAsync(
        IReadOnlyCollection<DepartmentId> departmentIds, CancellationToken cancellationToken) =>
        await dbContext.Departments
            .AsNoTracking()
            .Where(department => departmentIds.Contains(department.Id))
            .ToArrayAsync(cancellationToken);

    // ILike y no ToLower(): evita los analizadores de sensibilidad cultural (CA1304/CA1311/
    // CA1862) y es la comparacion case-insensitive nativa de Npgsql. Sin comodines en el patron
    // (se escapan los que traiga el nombre) es una igualdad exacta insensible a mayusculas.
    public Task<Department?> FindByNameAsync(string name, CancellationToken cancellationToken)
    {
        var pattern = EscapeLikeWildcards(name.Trim());
        return dbContext.Departments
            .AsNoTracking()
            .SingleOrDefaultAsync(
                department => EF.Functions.ILike(department.Name, pattern, LikeEscapeCharacter),
                cancellationToken);
    }
}
