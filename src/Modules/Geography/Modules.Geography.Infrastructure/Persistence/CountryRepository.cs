using Microsoft.EntityFrameworkCore;
using Modules.Geography.Application;
using Modules.Geography.Domain;

namespace Modules.Geography.Infrastructure.Persistence;

internal sealed class CountryRepository(GeographyDbContext dbContext) : ICountryRepository
{
    public async Task<IReadOnlyList<Country>> ListAllAsync(CancellationToken cancellationToken) =>
        await dbContext.Countries
            .AsNoTracking()
            .ToArrayAsync(cancellationToken);
}
