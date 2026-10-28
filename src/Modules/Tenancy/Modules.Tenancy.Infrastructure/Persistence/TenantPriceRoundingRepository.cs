using Microsoft.EntityFrameworkCore;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Infrastructure.Persistence;

internal sealed class TenantPriceRoundingRepository(TenancyDbContext dbContext) : ITenantPriceRoundingRepository
{
    public async Task<IReadOnlyList<TenantPriceRounding>> ListByTenantAsync(
        TenantId tenantId, CancellationToken cancellationToken) =>
        await dbContext.TenantPriceRoundings
            .Where(rounding => rounding.TenantId == tenantId)
            .ToListAsync(cancellationToken);

    public void Add(TenantPriceRounding rounding) => dbContext.TenantPriceRoundings.Add(rounding);

    public void Remove(TenantPriceRounding rounding) => dbContext.TenantPriceRoundings.Remove(rounding);
}
