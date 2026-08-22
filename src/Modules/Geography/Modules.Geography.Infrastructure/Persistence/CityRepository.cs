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
}
