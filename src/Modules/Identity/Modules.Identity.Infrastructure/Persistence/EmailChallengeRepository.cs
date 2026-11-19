using Microsoft.EntityFrameworkCore;
using Modules.Identity.Application;
using Modules.Identity.Domain;

namespace Modules.Identity.Infrastructure.Persistence;

internal sealed class EmailChallengeRepository(IdentityDbContext dbContext) : IEmailChallengeRepository
{
    public void Add(EmailChallenge challenge) => dbContext.EmailChallenges.Add(challenge);

    public async Task<DateTimeOffset?> FindLatestIssuedAtAsync(string emailHash, CancellationToken cancellationToken) =>
        await dbContext.EmailChallenges.AsNoTracking()
            .Where(challenge => challenge.EmailHash == emailHash)
            .MaxAsync(challenge => (DateTimeOffset?)challenge.CreatedAt, cancellationToken);

    public async Task InvalidateUnusedAsync(string emailHash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var unused = await dbContext.EmailChallenges
            .Where(challenge => challenge.EmailHash == emailHash && challenge.UsedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var challenge in unused)
        {
            challenge.Invalidate(now);
        }
    }

    // Una sola sentencia atómica que se confirma sola, como EmailTokenRepository.TryConsumeAsync: el UPDATE toma el candado
    // de la fila, así que dos intentos concurrentes se cuentan los dos y un código acertado pasa una sola vez. En el SET,
    // `attempts` es el valor anterior a la sentencia. Un código errado suma un intento y, al llegar al tope, deja el desafío
    // usado: el siguiente intento ya no encuentra fila aunque traiga el código correcto. Se materializa con ToListAsync por
    // la misma razón que en EmailTokenRepository: Postgres rechaza un UPDATE ... RETURNING como subconsulta.
    public async Task<bool> TryConsumeAsync(
        string emailHash, string codeHash, int maxAttempts, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var matches = await dbContext.Database
            .SqlQuery<bool>(
                $"""
                UPDATE identity.email_challenges
                SET attempts = CASE WHEN code_hash = {codeHash} THEN attempts ELSE attempts + 1 END,
                    used_at = CASE WHEN code_hash = {codeHash} OR attempts + 1 >= {maxAttempts} THEN {now} ELSE NULL END
                WHERE email_hash = {emailHash} AND used_at IS NULL AND expires_at > {now} AND attempts < {maxAttempts}
                RETURNING code_hash = {codeHash} AS "Value"
                """)
            .ToListAsync(cancellationToken);
        return matches.Contains(true);
    }
}
