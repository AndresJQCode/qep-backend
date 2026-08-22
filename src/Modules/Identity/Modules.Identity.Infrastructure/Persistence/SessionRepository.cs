using Microsoft.EntityFrameworkCore;
using Modules.Identity.Application;
using Modules.Identity.Domain;

namespace Modules.Identity.Infrastructure.Persistence;

internal sealed class SessionRepository(IdentityDbContext dbContext) : ISessionRepository
{
    public Task<Session?> FindByTokenHashAsync(
        string tokenHash,
        CancellationToken cancellationToken) =>
        dbContext.Sessions.SingleOrDefaultAsync(
            session => session.TokenHash == tokenHash,
            cancellationToken);

    public async Task<IReadOnlyList<Session>> ListActiveByUserAsync(
        Guid userId,
        CancellationToken cancellationToken) =>
        await dbContext.Sessions
            .Where(session => session.UserId == new UserId(userId) && session.RevokedAt == null)
            .ToListAsync(cancellationToken);

    public void Add(Session session) => dbContext.Sessions.Add(session);
}
