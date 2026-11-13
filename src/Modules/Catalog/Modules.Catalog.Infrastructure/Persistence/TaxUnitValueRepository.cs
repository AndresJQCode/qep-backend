using Microsoft.EntityFrameworkCore;
using Modules.Catalog.Application;
using Modules.Catalog.Domain;

namespace Modules.Catalog.Infrastructure.Persistence;

internal sealed class TaxUnitValueRepository(CatalogDbContext dbContext) : ITaxUnitValueRepository
{
    public async Task<IReadOnlyList<TaxUnitValue>> ListAsync(string? country, CancellationToken cancellationToken)
    {
        var query = dbContext.TaxUnitValues.AsNoTracking();
        if (country is not null)
        {
            query = query.Where(value => value.Country == country);
        }

        return await query
            .OrderBy(value => value.Country)
            .ThenByDescending(value => value.Year)
            .ToListAsync(cancellationToken);
    }

    public Task<TaxUnitValue?> FindAsync(string country, int year, CancellationToken cancellationToken) =>
        dbContext.TaxUnitValues.SingleOrDefaultAsync(
            value => value.Country == country && value.Year == year,
            cancellationToken);

    public Task<bool> AnyAsync(string country, CancellationToken cancellationToken) =>
        dbContext.TaxUnitValues.AnyAsync(value => value.Country == country, cancellationToken);

    public void Add(TaxUnitValue value) => dbContext.TaxUnitValues.Add(value);
}
