using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Modules.Identity.Application;
using Modules.Identity.Domain;

namespace Modules.Identity.Infrastructure.Persistence;

internal sealed class EmailChallengeRepository(IdentityDbContext dbContext) : IEmailChallengeRepository
{
    public void Add(EmailChallenge challenge) => dbContext.EmailChallenges.Add(challenge);

    // pg_advisory_xact_lock de dos claves, (Namespace, hashtext(email_hash)): se suelta con el commit o el rollback. Si el
    // llamador ya abrió una transacción, el candado entra en ella y confirmar es trabajo suyo.
    public async Task<IEmailChallengeIssuanceLock> LockIssuanceAsync(string emailHash, CancellationToken cancellationToken)
    {
        var owned = dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        try
        {
            await dbContext.Database.ExecuteSqlAsync(
                $"SELECT pg_advisory_xact_lock({EmailChallengeIssuanceLock.Namespace}, hashtext({emailHash}))",
                cancellationToken);
        }
        catch
        {
            if (owned is not null)
            {
                await owned.DisposeAsync();
            }

            throw;
        }

        return new IssuanceLock(owned);
    }

    public async Task<IReadOnlyList<DateTimeOffset>> ListIssuedSinceAsync(
        string emailHash, DateTimeOffset since, CancellationToken cancellationToken) =>
        await dbContext.EmailChallenges.AsNoTracking()
            .Where(challenge => challenge.EmailHash == emailHash && challenge.CreatedAt > since)
            .Select(challenge => challenge.CreatedAt)
            .ToListAsync(cancellationToken);

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
    // Revisión final de la rama, hallazgo 1: sólo el desafío más reciente del correo, en cualquier estado. Con varios vivos,
    // cada intento probaba todos sus códigos a la vez; si el más reciente ya se usó o venció, uno anterior tampoco sirve.
    public async Task<bool> TryConsumeAsync(
        string emailHash, string codeHash, int maxAttempts, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var matches = await dbContext.Database
            .SqlQuery<bool>(
                $"""
                WITH newest AS (
                    SELECT id FROM identity.email_challenges
                    WHERE email_hash = {emailHash}
                    ORDER BY created_at DESC, id DESC
                    LIMIT 1
                )
                UPDATE identity.email_challenges AS challenge
                SET attempts = CASE WHEN challenge.code_hash = {codeHash} THEN challenge.attempts ELSE challenge.attempts + 1 END,
                    used_at = CASE WHEN challenge.code_hash = {codeHash} OR challenge.attempts + 1 >= {maxAttempts}
                        THEN {now} ELSE NULL END
                FROM newest
                WHERE challenge.id = newest.id
                  AND challenge.used_at IS NULL AND challenge.expires_at > {now} AND challenge.attempts < {maxAttempts}
                RETURNING challenge.code_hash = {codeHash} AS "Value"
                """)
            .ToListAsync(cancellationToken);
        return matches.Contains(true);
    }

    private sealed class IssuanceLock(IDbContextTransaction? owned) : IEmailChallengeIssuanceLock
    {
        public Task CommitAsync(CancellationToken cancellationToken) =>
            owned is null ? Task.CompletedTask : owned.CommitAsync(cancellationToken);

        // Sin commit, disponer la transacción la deshace y suelta el candado.
        public ValueTask DisposeAsync() => owned?.DisposeAsync() ?? ValueTask.CompletedTask;
    }
}

/// <summary>
/// Revisión final de la rama, hallazgo 1: el espacio del candado de emisión de desafíos de correo,
/// <c>pg_advisory_xact_lock(Namespace, hashtext(email_hash))</c>. No es una fecha a propósito: los demás candados de dos claves
/// toman la del día (<c>TenantChangeLock</c> 20261008, <c>ConversationBotLock</c> 20261022, <c>ShopifyTokenLock</c> 20261112,
/// <c>StartupLock</c> 20261113, <c>InstagramTokenLock</c> 20261114, <c>ConnectionSlotsLock</c> 20261115,
/// <c>CompanyDefaultTaxRates</c> y <c>CopilotAskLock</c> 20261117, Shopify 20261118, <c>SiigoInvoiceLock</c> 20261119), y una
/// rama en paralelo podría tomar la siguiente.
/// </summary>
internal static class EmailChallengeIssuanceLock
{
    public const int Namespace = 20261131;
}
