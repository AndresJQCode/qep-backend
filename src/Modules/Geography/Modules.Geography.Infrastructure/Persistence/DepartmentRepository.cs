using Microsoft.EntityFrameworkCore;
using Modules.Geography.Application;
using Modules.Geography.Domain;

namespace Modules.Geography.Infrastructure.Persistence;

internal sealed class DepartmentRepository(GeographyDbContext dbContext) : IDepartmentRepository
{
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

    // En memoria y no ILike: ILike ya cubria mayusculas pero no tildes ("Bogota" vs "BOGOTÁ"),
    // y Postgres no tiene una funcion nativa de "sin tildes" sin la extension `unaccent`. Son 33
    // departamentos — traerlos enteros y comparar con NameMatching.Normalize no pesa.
    public async Task<Department?> FindByNameAsync(string name, CancellationToken cancellationToken)
    {
        var target = NameMatching.Normalize(name);
        var departments = await dbContext.Departments.AsNoTracking().ToArrayAsync(cancellationToken);
        return departments.SingleOrDefault(
            department => NameMatching.Normalize(department.Name) == target);
    }
}
