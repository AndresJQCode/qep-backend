using Microsoft.EntityFrameworkCore;
using Modules.Buyers.Application;
using Modules.Buyers.Domain;

namespace Modules.Buyers.Infrastructure.Persistence;

internal sealed class BuyerProfileRepository(BuyersDbContext dbContext) : IBuyerProfileRepository
{
    public Task<BuyerProfile?> FindAsync(Guid accountId, CancellationToken cancellationToken) =>
        dbContext.Profiles.SingleOrDefaultAsync(profile => profile.AccountId == accountId, cancellationToken);

    public void Add(BuyerProfile profile) => dbContext.Profiles.Add(profile);

    public void Remove(BuyerProfile profile) => dbContext.Profiles.Remove(profile);
}
