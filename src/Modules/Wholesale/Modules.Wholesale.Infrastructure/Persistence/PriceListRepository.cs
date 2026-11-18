using Microsoft.EntityFrameworkCore;
using Modules.Wholesale.Application;
using Modules.Wholesale.Domain;

namespace Modules.Wholesale.Infrastructure.Persistence;

internal sealed class PriceListRepository(WholesaleDbContext dbContext) : IPriceListRepository
{
    public Task<PriceList?> GetAsync(Guid tenantId, PriceListId id, CancellationToken cancellationToken) =>
        dbContext.PriceLists.FirstOrDefaultAsync(list => list.TenantId == tenantId && list.Id == id, cancellationToken);

    public async Task<IReadOnlyList<PriceList>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await dbContext.PriceLists.AsNoTracking()
            .Where(list => list.TenantId == tenantId)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<PriceListId, PriceList>> FindManyAsync(
        Guid tenantId, IReadOnlyCollection<PriceListId> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return new Dictionary<PriceListId, PriceList>();
        }

        var wanted = ids.Distinct().ToArray();
        return await dbContext.PriceLists.AsNoTracking()
            .Where(list => list.TenantId == tenantId && wanted.Contains(list.Id))
            .ToDictionaryAsync(list => list.Id, cancellationToken);
    }

    // La llave del índice único (normalized_name = lower(name)). La minúscula invariante de .NET y lower() de Postgres
    // coinciden en todo texto que no sea exótico; donde no, el índice sigue siendo la red y sale el mismo 409.
    public Task<bool> NameExistsAsync(Guid tenantId, string name, PriceListId? except, CancellationToken cancellationToken)
    {
        var normalized = name.Trim().ToLowerInvariant();
        var lists = dbContext.PriceLists.AsNoTracking()
            .Where(list => list.TenantId == tenantId
                && EF.Property<string>(list, WholesaleDbContext.NormalizedName) == normalized);
        if (except is { } excluded)
        {
            lists = lists.Where(list => list.Id != excluded);
        }

        return lists.AnyAsync(cancellationToken);
    }

    public void Add(PriceList list) => dbContext.PriceLists.Add(list);

    public void Remove(PriceList list) => dbContext.PriceLists.Remove(list);
}
