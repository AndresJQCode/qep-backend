using Microsoft.EntityFrameworkCore;
using Modules.Geography.Application;
using Modules.Geography.Domain;

namespace Modules.Geography.Infrastructure.Persistence;

// Con seguimiento: lo que se lee acá se cambia y se guarda con IGeographyUnitOfWork.
internal sealed class ForeignCitySynonymRepository(GeographyDbContext dbContext) : IForeignCitySynonymRepository
{
    public Task<ForeignCitySynonym?> FindAsync(ForeignCitySynonymId id, CancellationToken cancellationToken) =>
        dbContext.ForeignCitySynonyms.SingleOrDefaultAsync(synonym => synonym.Id == id, cancellationToken);

    public Task<ForeignCitySynonym?> FindActiveAsync(
        string countryCode, string normalizedText, CancellationToken cancellationToken) =>
        dbContext.ForeignCitySynonyms.SingleOrDefaultAsync(
            synonym =>
                synonym.CountryCode == countryCode &&
                synonym.NormalizedText == normalizedText &&
                synonym.DeletedAt == null,
            cancellationToken);

    public void Add(ForeignCitySynonym synonym) => dbContext.ForeignCitySynonyms.Add(synonym);

    public Task RefreshAsync(ForeignCitySynonym synonym, CancellationToken cancellationToken) =>
        dbContext.Entry(synonym).ReloadAsync(cancellationToken);

    public async Task<(IReadOnlyList<ForeignCitySynonym> Items, int Total)> ListAsync(
        string? countryCode,
        string? normalizedSearch,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = dbContext.ForeignCitySynonyms.AsNoTracking().Where(synonym => synonym.DeletedAt == null);
        if (countryCode is not null)
        {
            query = query.Where(synonym => synonym.CountryCode == countryCode);
        }

        // Contains se traduce a strpos, así que % y _ del buscador son texto. El nombre de la ciudad ya está normalizado
        // en la base (billing_cities.normalized_name), así que se busca ahí sin traer nada a memoria.
        if (!string.IsNullOrEmpty(normalizedSearch))
        {
            query = query.Where(synonym =>
                synonym.NormalizedText.Contains(normalizedSearch)
                || dbContext.BillingCities.Any(city =>
                    city.Id == synonym.BillingCityId && city.NormalizedName.Contains(normalizedSearch)));
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(synonym => synonym.UpdatedAt)
            .ThenByDescending(synonym => synonym.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return (items, total);
    }
}
