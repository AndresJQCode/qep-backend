using Microsoft.EntityFrameworkCore;
using Modules.Store.Application;
using Modules.Store.Domain;

namespace Modules.Store.Infrastructure.Persistence;

internal sealed class StoreOrderConsentRepository(StoreDbContext dbContext) : IStoreOrderConsents
{
    public void Add(StoreOrderConsent consent) => dbContext.OrderConsents.Add(consent);

    public Task<StoreOrderConsent?> FindAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken) =>
        dbContext.OrderConsents
            .AsNoTracking()
            .FirstOrDefaultAsync(value => value.TenantId == tenantId && value.OrderId == orderId, cancellationToken);
}
