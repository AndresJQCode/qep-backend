using Microsoft.EntityFrameworkCore;
using Modules.ServiceContracts.Application;
using Modules.ServiceContracts.Domain;

namespace Modules.ServiceContracts.Infrastructure.Persistence;

internal sealed class CustomerBillingProfileRepository(ServiceContractsDbContext dbContext)
    : ICustomerBillingProfileRepository
{
    public Task<CustomerBillingProfile?> FindAsync(
        Guid tenantId, Guid customerId, CancellationToken cancellationToken) =>
        dbContext.Profiles.FirstOrDefaultAsync(
            profile => profile.TenantId == tenantId && profile.CustomerId == customerId, cancellationToken);

    // Sin seguimiento: el worker sólo lee canal y encargado, y no hay razón para rastrear un perfil por cliente.
    public async Task<IReadOnlyList<CustomerBillingProfile>> ListAsync(
        Guid tenantId, CancellationToken cancellationToken) =>
        await dbContext.Profiles
            .AsNoTracking()
            .Where(profile => profile.TenantId == tenantId)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<CustomerBillingProfile>> ListByCustomersAsync(
        Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken)
    {
        if (customerIds.Count == 0)
        {
            return [];
        }

        var ids = customerIds.ToArray();
        return await dbContext.Profiles
            .AsNoTracking()
            .Where(profile => profile.TenantId == tenantId && ids.Contains(profile.CustomerId))
            .ToListAsync(cancellationToken);
    }

    public void Add(CustomerBillingProfile profile) => dbContext.Profiles.Add(profile);
}
