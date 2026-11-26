using Microsoft.EntityFrameworkCore;
using Modules.Buyers.Application;
using Modules.Buyers.Domain;

namespace Modules.Buyers.Infrastructure.Persistence;

internal sealed class BuyerEmailTokenRepository(BuyersDbContext dbContext) : IBuyerEmailTokenRepository
{
    public void Add(BuyerEmailToken token) => dbContext.EmailTokens.Add(token);

    public async Task InvalidateUnusedAsync(
        Guid accountId, BuyerEmailTokenPurpose purpose, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var unused = await dbContext.EmailTokens
            .Where(token => token.AccountId == accountId && token.Purpose == purpose && token.UsedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var token in unused)
        {
            token.Invalidate(now);
        }
    }

    // Una sola sentencia atómica que se confirma sola (D6): el UPDATE ... WHERE used_at IS NULL deja pasar a un único
    // request concurrente. El alias "Value" es el nombre de columna que EF espera en SqlQuery<T> de un escalar. Se
    // materializa con ToListAsync: cualquier otra composición envuelve el SQL en una subconsulta, y Postgres rechaza un
    // UPDATE ... RETURNING como subconsulta.
    public async Task<Guid?> TryConsumeAsync(
        string tokenHash, BuyerEmailTokenPurpose purpose, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var purposeValue = purpose.ToString();
        var rows = await dbContext.Database
            .SqlQuery<Guid>(
                $"""
                UPDATE buyers.email_tokens SET used_at = {now}
                WHERE token_hash = {tokenHash} AND purpose = {purposeValue}
                  AND used_at IS NULL AND expires_at > {now}
                RETURNING account_id AS "Value"
                """)
            .ToListAsync(cancellationToken);
        return rows.Count == 0 ? null : rows.Single();
    }
}
