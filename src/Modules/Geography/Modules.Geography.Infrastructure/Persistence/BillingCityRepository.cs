using Microsoft.EntityFrameworkCore;
using Modules.Geography.Application;
using Modules.Geography.Domain;

namespace Modules.Geography.Infrastructure.Persistence;

// Sin seguimiento: el catálogo lo escribe el importador y acá sólo se lee.
internal sealed class BillingCityRepository(GeographyDbContext dbContext) : IBillingCityRepository
{
    public Task<bool> CountryExistsAsync(string countryCode, CancellationToken cancellationToken) =>
        dbContext.BillingCities.AnyAsync(city => city.CountryCode == countryCode, cancellationToken);

    public async Task<IReadOnlyList<BillingCity>> MatchByNameAsync(
        string countryCode, string normalizedName, CancellationToken cancellationToken) =>
        await dbContext.BillingCities
            .AsNoTracking()
            .Where(city => city.CountryCode == countryCode && city.NormalizedName == normalizedName)
            .OrderBy(city => city.StateName)
            .ThenBy(city => city.CityCode)
            .ToListAsync(cancellationToken);

    public Task<BillingCity?> FindAsync(BillingCityId id, CancellationToken cancellationToken) =>
        dbContext.BillingCities.AsNoTracking().SingleOrDefaultAsync(city => city.Id == id, cancellationToken);

    public async Task<IReadOnlyList<BillingCity>> ListByIdsAsync(
        IReadOnlyCollection<BillingCityId> ids, CancellationToken cancellationToken) =>
        await dbContext.BillingCities
            .AsNoTracking()
            .Where(city => ids.Contains(city.Id))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<BillingCity>> ListByCountriesAsync(
        IReadOnlyCollection<string> countryCodes, CancellationToken cancellationToken) =>
        await dbContext.BillingCities
            .AsNoTracking()
            .Where(city => countryCodes.Contains(city.CountryCode))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<BillingCity>> SearchAsync(
        string countryCode, string? normalizedSearch, int limit, CancellationToken cancellationToken)
    {
        var query = dbContext.BillingCities.AsNoTracking().Where(city => city.CountryCode == countryCode);

        // Contains se traduce a strpos, así que un % o un _ del buscador son texto y no comodines.
        if (!string.IsNullOrEmpty(normalizedSearch))
        {
            query = query.Where(city => city.NormalizedName.Contains(normalizedSearch));
        }

        return await query
            .OrderBy(city => city.NormalizedName)
            .ThenBy(city => city.StateName)
            .ThenBy(city => city.CityCode)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<BillingCountry>> ListCountriesAsync(CancellationToken cancellationToken)
    {
        var countries = await dbContext.BillingCities
            .AsNoTracking()
            .Select(city => new { city.CountryCode, city.CountryName })
            .Distinct()
            .OrderBy(country => country.CountryName)
            .ThenBy(country => country.CountryCode)
            .ToListAsync(cancellationToken);
        return countries.Select(country => new BillingCountry(country.CountryCode, country.CountryName)).ToList();
    }
}
