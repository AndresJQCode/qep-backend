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
}
