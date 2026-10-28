using Microsoft.EntityFrameworkCore;
using Modules.Identity.Application;
using Modules.Identity.Domain;

namespace Modules.Identity.Infrastructure.Persistence;

internal sealed class EmailTokenRepository(IdentityDbContext dbContext) : IEmailTokenRepository
{
    public void Add(EmailToken token) => dbContext.EmailTokens.Add(token);

    public async Task InvalidateUnusedAsync(
        Guid userId,
        EmailTokenPurpose? purpose,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var id = new UserId(userId);
        var query = dbContext.EmailTokens.Where(token => token.UserId == id && token.UsedAt == null);
        if (purpose is { } only)
        {
            query = query.Where(token => token.Purpose == only);
        }

        foreach (var token in await query.ToListAsync(cancellationToken))
        {
            token.Invalidate(now);
        }
    }

    // Una sola sentencia atómica que se confirma sola (D6): el UPDATE ... WHERE used_at IS NULL
    // deja pasar a un único request concurrente. El alias "Value" es el nombre de columna que EF
    // espera en SqlQuery<T> de un escalar. Se materializa con ToListAsync: cualquier otra
    // composición (FirstOrDefault) envuelve el SQL en una subconsulta, y Postgres rechaza un
    // UPDATE ... RETURNING como subconsulta.
    public async Task<Guid?> TryConsumeAsync(
        string tokenHash,
        EmailTokenPurpose purpose,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var purposeValue = PurposeToColumn(purpose);
        var rows = await dbContext.Database
            .SqlQuery<Guid>(
                $"""
                UPDATE identity.email_tokens SET used_at = {now}
                WHERE token_hash = {tokenHash} AND purpose = {purposeValue}
                  AND used_at IS NULL AND expires_at > {now}
                RETURNING user_id AS "Value"
                """)
            .ToListAsync(cancellationToken);
        return rows.Count == 0 ? null : rows.Single();
    }

    // La misma conversión que el mapeo de la columna.
    internal static string PurposeToColumn(EmailTokenPurpose purpose) => purpose.ToString().ToLowerInvariant();

    internal static EmailTokenPurpose PurposeFromColumn(string value) =>
        Enum.Parse<EmailTokenPurpose>(value, ignoreCase: true);
}