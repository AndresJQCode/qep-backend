using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Buyers.Application;
using Modules.Buyers.Domain;

namespace Modules.Buyers.Infrastructure.Persistence;

internal sealed class BuyerSessionRepository(BuyersDbContext dbContext, IClock clock) : IBuyerSessionRepository
{
    public void Add(BuyerSession session) => dbContext.Sessions.Add(session);

    public Task<BuyerSession?> FindByTokenHashAsync(string hash, CancellationToken cancellationToken) =>
        dbContext.Sessions.SingleOrDefaultAsync(session => session.TokenHash == hash, cancellationToken);

    public async Task<IReadOnlyList<BuyerSession>> ListActiveAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        return await dbContext.Sessions
            .Where(session => session.AccountId == accountId && session.RevokedAt == null && session.ExpiresAt > now)
            .ToListAsync(cancellationToken);
    }

    // Masiva y no por entidad: en READ COMMITTED la sentencia ve toda sesión confirmada antes de ella, también la de un login que
    // confirmó después de que se leyó la cuenta. Las mismas columnas que BuyerSession.Revoke.
    public Task RevokeActiveAsync(
        Guid accountId, Guid? keepSessionId, DateTimeOffset now, string reason, CancellationToken cancellationToken) =>
        dbContext.Sessions
            .Where(session => session.AccountId == accountId
                && session.RevokedAt == null
                && (keepSessionId == null || session.Id != keepSessionId))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(session => session.RevokedAt, now)
                    .SetProperty(session => session.RevokedReason, reason),
                cancellationToken);
}
