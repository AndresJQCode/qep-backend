using Microsoft.EntityFrameworkCore;
using Modules.Identity.Application;
using Modules.Identity.Domain;

namespace Modules.Identity.Infrastructure.Persistence;

internal sealed class TermsAcceptanceRepository(IdentityDbContext dbContext) : ITermsAcceptanceRepository
{
    // Una sola sentencia que se confirma sola: ON CONFLICT sobre el índice único (user_id, version) hace idempotentes también
    // dos aceptaciones concurrentes, sin traducir un 23505.
    public async Task<bool> AddIfMissingAsync(TermsAcceptance acceptance, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(acceptance);
        var inserted = await dbContext.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO identity.terms_acceptances (id, user_id, version, accepted_at)
            VALUES ({acceptance.Id.Value}, {acceptance.UserId.Value}, {acceptance.Version}, {acceptance.AcceptedAt})
            ON CONFLICT (user_id, version) DO NOTHING
            """,
            cancellationToken);
        return inserted == 1;
    }

    public Task<bool> ExistsAsync(Guid userId, string version, CancellationToken cancellationToken)
    {
        var id = new UserId(userId);
        return dbContext.TermsAcceptances.AsNoTracking()
            .AnyAsync(acceptance => acceptance.UserId == id && acceptance.Version == version, cancellationToken);
    }
}
