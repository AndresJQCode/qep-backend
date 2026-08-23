using Microsoft.EntityFrameworkCore;
using Modules.Pricing.Application;
using Modules.Pricing.Domain;

namespace Modules.Pricing.Infrastructure.Persistence;

internal sealed class PriceListRepository(PricingDbContext dbContext) : IPriceListRepository
{
    public async Task<IReadOnlyList<PriceList>> ListAsync(
        Guid tenantId,
        CancellationToken cancellationToken) =>
        await dbContext.PriceLists
            .AsNoTracking()
            .Where(priceList => priceList.TenantId == tenantId)
            .OrderBy(priceList => priceList.Name)
            .ToListAsync(cancellationToken);

    // Con tracking a proposito, a diferencia de ListAsync: los llamadores de este mutan el
    // agregado y dependen de la unidad de trabajo para persistirlo.
    public Task<PriceList?> FindAsync(
        Guid tenantId,
        PriceListId priceListId,
        CancellationToken cancellationToken) =>
        dbContext.PriceLists.SingleOrDefaultAsync(
            priceList => priceList.TenantId == tenantId && priceList.Id == priceListId,
            cancellationToken);

    public async Task<IReadOnlyList<PriceList>> ListByIdsAsync(
        Guid tenantId,
        IReadOnlyCollection<PriceListId> priceListIds,
        CancellationToken cancellationToken) =>
        await dbContext.PriceLists
            .AsNoTracking()
            .Where(priceList =>
                priceList.TenantId == tenantId && priceListIds.Contains(priceList.Id))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<PriceList>> ListByIdsAsync(
        IReadOnlyCollection<PriceListId> priceListIds,
        CancellationToken cancellationToken) =>
        await dbContext.PriceLists
            .AsNoTracking()
            .Where(priceList => priceListIds.Contains(priceList.Id))
            .ToListAsync(cancellationToken);

    public void Add(PriceList priceList) => dbContext.PriceLists.Add(priceList);

    public void Remove(PriceList priceList) => dbContext.PriceLists.Remove(priceList);
}
