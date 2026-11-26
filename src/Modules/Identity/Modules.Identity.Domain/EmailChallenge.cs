namespace Modules.Identity.Domain;

public readonly record struct EmailChallengeId(Guid Value)
{
    public static EmailChallengeId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

/// <summary>
/// Spec 2026-11-19 (onboarding por link) §4: un código de un solo uso mandado a un correo que todavía no es de ningún
/// usuario. Por eso no sirve <see cref="EmailToken"/>, que cuelga de un usuario. Se guardan sólo el hash del correo
/// normalizado y el del código. El consumo real (y la cuenta de intentos) es atómico en base de datos
/// (<c>IEmailChallengeRepository.TryConsumeAsync</c>); esta entidad modela la emisión y la invalidación.
/// </summary>
public sealed class EmailChallenge
{
    private EmailChallenge()
    {
    }

    private EmailChallenge(
        EmailChallengeId id,
        string emailHash,
        string codeHash,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt)
    {
        Id = id;
        EmailHash = emailHash;
        CodeHash = codeHash;
        CreatedAt = issuedAt;
        ExpiresAt = expiresAt;
    }

    public EmailChallengeId Id { get; private set; }

    public string EmailHash { get; private set; } = string.Empty;

    public string CodeHash { get; private set; } = string.Empty;

    /// <summary>Intentos fallidos. Al llegar al tope el desafío queda usado (inválido).</summary>
    public int Attempts { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>Cuándo se consumió o se invalidó: por un código nuevo o por agotar los intentos.</summary>
    public DateTimeOffset? UsedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static EmailChallenge Issue(
        EmailChallengeId id,
        string emailHash,
        string codeHash,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt)
    {
        if (string.IsNullOrWhiteSpace(emailHash) || string.IsNullOrWhiteSpace(codeHash))
        {
            throw new IdentityDomainException(
                "identity.email_challenge.hash_required",
                "An email challenge needs non-empty hashes.");
        }

        if (expiresAt <= issuedAt)
        {
            throw new IdentityDomainException(
                "identity.email_challenge.expiry_invalid",
                "An email challenge must expire after it is issued.");
        }

        return new EmailChallenge(id, emailHash, codeHash, issuedAt, expiresAt);
    }

    /// <summary>Lo deja inusable sin consumirlo. Es idempotente: no pisa un <c>UsedAt</c> previo.</summary>
    public void Invalidate(DateTimeOffset occurredAt) => UsedAt ??= occurredAt;
}
