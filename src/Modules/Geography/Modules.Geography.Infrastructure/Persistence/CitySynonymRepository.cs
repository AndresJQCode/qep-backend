using Microsoft.EntityFrameworkCore;
using Modules.Geography.Application;
using Modules.Geography.Domain;

namespace Modules.Geography.Infrastructure.Persistence;

// Con seguimiento: lo que se lee acá se cambia y se guarda con IGeographyUnitOfWork.
internal sealed class CitySynonymRepository(GeographyDbContext dbContext) : ICitySynonymRepository
{
    public Task<CitySynonym?> FindAsync(CitySynonymId id, CancellationToken cancellationToken) =>
        dbContext.CitySynonyms.SingleOrDefaultAsync(synonym => synonym.Id == id, cancellationToken);

    public Task<CitySynonym?> FindActiveAsync(
        DepartmentId departmentId, string normalizedText, CancellationToken cancellationToken) =>
        dbContext.CitySynonyms.SingleOrDefaultAsync(
            synonym =>
                synonym.DepartmentId == departmentId &&
                synonym.NormalizedText == normalizedText &&
                synonym.DeletedAt == null,
            cancellationToken);

    public void Add(CitySynonym synonym) => dbContext.CitySynonyms.Add(synonym);

    public async Task<(IReadOnlyList<CitySynonym> Items, int Total)> ListAsync(
        DepartmentId? departmentId,
        string? normalizedSearch,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = dbContext.CitySynonyms.AsNoTracking().Where(synonym => synonym.DeletedAt == null);
        if (departmentId is { } department)
        {
            query = query.Where(synonym => synonym.DepartmentId == department);
        }

        if (!string.IsNullOrEmpty(normalizedSearch))
        {
            // El nombre de la ciudad se normaliza en memoria (tildes: Postgres no tiene unaccent aquí) sobre las ~1.100 ciudades
            // de DIVIPOLA, y se busca por sus ids; Contains se traduce a strpos, así que % y _ del buscador son texto.
            var cityIds = (await dbContext.Cities.AsNoTracking()
                    .Select(city => new { city.Id, city.Name, city.DivipolaName })
                    .ToListAsync(cancellationToken))
                .Where(city => NameMatching.Normalize(city.Name).Contains(normalizedSearch, StringComparison.Ordinal)
                    || NameMatching.Normalize(city.DivipolaName).Contains(normalizedSearch, StringComparison.Ordinal))
                .Select(city => city.Id)
                .ToList();
            query = query.Where(synonym => synonym.NormalizedText.Contains(normalizedSearch) || cityIds.Contains(synonym.CityId));
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
