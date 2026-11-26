using Microsoft.EntityFrameworkCore;
using Modules.ServiceContracts.Application;
using Modules.ServiceContracts.Domain;

namespace Modules.ServiceContracts.Infrastructure.Persistence;

internal sealed class ServicePriceChangeRepository(ServiceContractsDbContext dbContext) : IServicePriceChangeRepository
{
    public void Add(ServicePriceChange change) => dbContext.ServicePriceChanges.Add(change);

    public async Task RemoveByServiceAsync(ServiceContractId serviceId, CancellationToken cancellationToken) =>
        dbContext.ServicePriceChanges.RemoveRange(
            await dbContext.ServicePriceChanges.Where(change => change.ServiceId == serviceId).ToListAsync(cancellationToken));

    // Id de desempate: dos cambios en el mismo instante (una prueba, un reloj grueso) salen en un orden estable.
    public async Task<IReadOnlyList<ServicePriceChange>> ListByServiceAsync(
        ServiceContractId serviceId, CancellationToken cancellationToken) =>
        await dbContext.ServicePriceChanges
            .AsNoTracking()
            .Where(change => change.ServiceId == serviceId)
            .OrderByDescending(change => change.OccurredAt)
            .ThenByDescending(change => change.Id)
            .ToListAsync(cancellationToken);
}
