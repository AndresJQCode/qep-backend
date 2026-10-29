using Microsoft.EntityFrameworkCore;
using Modules.Billing.Application;
using Modules.Billing.Domain;

namespace Modules.Billing.Infrastructure.Persistence;

internal sealed class CustomerBillingProfileRepository(BillingDbContext dbContext)
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

    public void Add(CustomerBillingProfile profile) => dbContext.Profiles.Add(profile);
}
