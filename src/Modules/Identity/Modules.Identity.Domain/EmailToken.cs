namespace Modules.Identity.Domain;

public readonly record struct EmailTokenId(Guid Value)
{
    public static EmailTokenId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

public enum EmailTokenPurpose
{
    Verify = 1,
    Reset = 2,
}

/// <summary>
/// Token de un solo uso enviado por email (verificar el correo o restablecer la contraseña).
/// Sólo se persiste el hash del valor crudo; el consumo real es atómico en base de datos
/// (<c>IEmailTokenRepository.TryConsumeAsync</c>), esta entidad modela emisión e invalidación.
/// </summary>
public sealed class EmailToken
{
    public static readonly TimeSpan VerifyLifetime = TimeSpan.FromHours(24);

    public static readonly TimeSpan ResetLifetime = TimeSpan.FromHours(1);

    private EmailToken()
    {
    }

    private EmailToken(
        EmailTokenId id,
        UserId userId,
        EmailTokenPurpose purpose,
        string tokenHash,
        DateTimeOffset issuedAt)
    {
        Id = id;
        UserId = userId;
        Purpose = purpose;
        TokenHash = tokenHash;
        CreatedAt = issuedAt;
        ExpiresAt = issuedAt + LifetimeOf(purpose);
    }

    public EmailTokenId Id { get; private set; }

    public UserId UserId { get; private set; }

    public EmailTokenPurpose Purpose { get; private set; }

    public string TokenHash { get; private set; } = string.Empty;

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? UsedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static TimeSpan LifetimeOf(EmailTokenPurpose purpose) => purpose switch
    {
        EmailTokenPurpose.Verify => VerifyLifetime,
        EmailTokenPurpose.Reset => ResetLifetime,
        _ => throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "Unknown email token purpose."),
    };

    public static EmailToken Issue(
        EmailTokenId id,
        UserId userId,
        EmailTokenPurpose purpose,
        string tokenHash,
        DateTimeOffset issuedAt)
    {
        if (string.IsNullOrWhiteSpace(tokenHash))
        {
            throw new IdentityDomainException(
                "identity.email_token.hash_required",
                "An email token needs a non-empty hash.");
        }

        return new EmailToken(id, userId, purpose, tokenHash, issuedAt);
    }

    public bool IsUsable(DateTimeOffset now) => UsedAt is null && now < ExpiresAt;

    /// <summary>Lo deja inusable sin consumirlo. Es idempotente: no pisa un <c>UsedAt</c> previo.</summary>
    public void Invalidate(DateTimeOffset occurredAt) => UsedAt ??= occurredAt;
}