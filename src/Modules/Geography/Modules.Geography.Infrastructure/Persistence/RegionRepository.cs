using Microsoft.EntityFrameworkCore;
using Modules.Geography.Application;
using Modules.Geography.Domain;

namespace Modules.Geography.Infrastructure.Persistence;

internal sealed class RegionRepository(GeographyDbContext dbContext) : IRegionRepository
{
    public async Task<IReadOnlyList<Region>> ListByCountryAsync(
        string countryCode, CancellationToken cancellationToken) =>
        await dbContext.Regions
            .AsNoTracking()
            .Where(region => region.CountryCode == countryCode)
            .ToArrayAsync(cancellationToken);

    public async Task<IReadOnlyList<Region>> ListByIdsAsync(
        IReadOnlyCollection<RegionId> regionIds, CancellationToken cancellationToken) =>
        await dbContext.Regions
            .AsNoTracking()
            .Where(region => regionIds.Contains(region.Id))
            .ToArrayAsync(cancellationToken);

    public Task<Region?> FindAsync(RegionId regionId, CancellationToken cancellationToken) =>
        dbContext.Regions
            .AsNoTracking()
            .SingleOrDefaultAsync(region => region.Id == regionId, cancellationToken);

    public async Task<IReadOnlyList<RegionCity>> ListCitiesAsync(
        RegionId regionId, CancellationToken cancellationToken) =>
        await dbContext.RegionCities
            .AsNoTracking()
            .Where(city => city.RegionId == regionId)
            .ToArrayAsync(cancellationToken);
}
