using Microsoft.EntityFrameworkCore;
using Modules.ServiceContracts.Application;
using Modules.ServiceContracts.Domain;

namespace Modules.ServiceContracts.Infrastructure.Persistence;

internal sealed class IpcRateRepository(ServiceContractsDbContext dbContext) : IIpcRateRepository
{
    public void Add(IpcRate rate) => dbContext.IpcRates.Add(rate);

    public Task<IpcRate?> FindAsync(Guid tenantId, int year, CancellationToken cancellationToken) =>
        dbContext.IpcRates.FirstOrDefaultAsync(rate => rate.TenantId == tenantId && rate.Year == year, cancellationToken);

    public async Task<IReadOnlyList<IpcRate>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await dbContext.IpcRates
            .AsNoTracking()
            .Where(rate => rate.TenantId == tenantId)
            .OrderByDescending(rate => rate.Year)
            .ToListAsync(cancellationToken);
}
