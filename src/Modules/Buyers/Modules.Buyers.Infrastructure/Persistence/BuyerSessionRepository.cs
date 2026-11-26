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
    // confirmó después de que se leyó la cuenta. Las mismas columnas que BuyerSession.Revoke. SQL crudo y no ExecuteUpdateAsync
    // porque la caché de sesión (spec 2026-11-13, D-4; plan P4) necesita los hashes revocados, y ExecuteUpdate no tiene RETURNING.
    public async Task<IReadOnlyList<RevokedBuyerSession>> RevokeActiveAsync(
        Guid accountId, Guid? keepSessionId, DateTimeOffset now, string reason, CancellationToken cancellationToken)
    {
        var rows = await dbContext.Database
            .SqlQuery<RevokedRow>(
                $"""
                UPDATE buyers.sessions
                SET revoked_at = {now}, revoked_reason = {reason}
                WHERE account_id = {accountId}
                  AND revoked_at IS NULL
                  AND ({keepSessionId}::uuid IS NULL OR id <> {keepSessionId}::uuid)
                RETURNING id AS "SessionId", token_hash AS "TokenHash"
                """)
            .ToListAsync(cancellationToken);
        return rows.Select(row => new RevokedBuyerSession(row.SessionId, row.TokenHash)).ToList();
    }

    private sealed class RevokedRow
    {
        public Guid SessionId { get; init; }

        public string TokenHash { get; init; } = string.Empty;
    }
}
