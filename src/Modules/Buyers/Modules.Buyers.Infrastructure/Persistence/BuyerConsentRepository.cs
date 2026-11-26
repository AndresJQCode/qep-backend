using Modules.Buyers.Application;
using Modules.Buyers.Domain;

namespace Modules.Buyers.Infrastructure.Persistence;

internal sealed class BuyerConsentRepository(BuyersDbContext dbContext) : IBuyerConsentRepository
{
    public void Add(BuyerConsent consent) => dbContext.Consents.Add(consent);
}
