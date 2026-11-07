using Microsoft.EntityFrameworkCore;
using Modules.Buyers.Application;
using Modules.Buyers.Domain;

namespace Modules.Buyers.Infrastructure.Persistence;

internal sealed class BuyerAccountRepository(BuyersDbContext dbContext) : IBuyerAccountRepository
{
    public Task<BuyerAccount?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken) =>
        dbContext.Accounts.SingleOrDefaultAsync(account => account.Email == normalizedEmail, cancellationToken);

    public Task<BuyerAccount?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.Accounts.SingleOrDefaultAsync(account => account.Id == id, cancellationToken);

    public void Add(BuyerAccount account) => dbContext.Accounts.Add(account);

    public void Remove(BuyerAccount account) => dbContext.Accounts.Remove(account);
}
