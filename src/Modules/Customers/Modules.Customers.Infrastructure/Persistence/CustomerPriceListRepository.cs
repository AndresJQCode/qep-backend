using Microsoft.EntityFrameworkCore;
using Modules.Customers.Application;
using Modules.Customers.Domain;

namespace Modules.Customers.Infrastructure.Persistence;

internal sealed class CustomerPriceListRepository(CustomersDbContext dbContext)
    : ICustomerPriceListRepository
{
    public async Task<IReadOnlyList<CustomerPriceList>> ListAsync(
        Guid tenantId,
        CustomerId customerId,
        CancellationToken cancellationToken) =>
        await dbContext.CustomerPriceLists
            .Where(assignment =>
                assignment.TenantId == tenantId && assignment.CustomerId == customerId)
            .ToListAsync(cancellationToken);

    public void Add(CustomerPriceList assignment) => dbContext.CustomerPriceLists.Add(assignment);

    public void Remove(CustomerPriceList assignment) =>
        dbContext.CustomerPriceLists.Remove(assignment);

    public Task<bool> AnyWithPriceListAsync(
        Guid tenantId,
        Guid priceListId,
        CancellationToken cancellationToken) =>
        dbContext.CustomerPriceLists.AnyAsync(
            assignment => assignment.TenantId == tenantId && assignment.PriceListId == priceListId,
            cancellationToken);
}
