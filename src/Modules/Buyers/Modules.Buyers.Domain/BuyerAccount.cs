namespace Modules.Buyers.Domain;

public sealed class BuyerAccount
{
    /// <summary>Una cuenta sin verificar se puede purgar pasado este tiempo.</summary>
    public static readonly TimeSpan UnverifiedRetention = TimeSpan.FromDays(7);

    private BuyerAccount()
    {
    }

    public Guid Id { get; private set; }
    public string Email { get; private set; } = string.Empty;
    public string PasswordHash { get; private set; } = string.Empty;
    public DateTimeOffset? EmailVerifiedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public bool IsVerified => EmailVerifiedAt is not null;

    public static BuyerAccount Register(Guid id, string normalizedEmail, string passwordHash, DateTimeOffset now) =>
        new()
        {
            Id = id,
            Email = normalizedEmail,
            PasswordHash = passwordHash,
            CreatedAt = now,
            UpdatedAt = now,
        };

    public void ReplacePendingRegistration(string passwordHash, DateTimeOffset now)
    {
        if (IsVerified)
            throw new InvalidOperationException("A verified account cannot be replaced by a new registration.");

        PasswordHash = passwordHash;
        UpdatedAt = now;
    }

    /// <summary>
    /// Un reenvío de la verificación cuenta como el último registro: reinicia <see cref="UnverifiedRetention"/>, para que la purga
    /// no mate el enlace recién enviado. Una cuenta verificada no cambia.
    /// </summary>
    public void MarkVerificationResent(DateTimeOffset now)
    {
        if (IsVerified) return;

        UpdatedAt = now;
    }

    public void MarkEmailVerified(DateTimeOffset now)
    {
        if (IsVerified) return;

        EmailVerifiedAt = now;
        UpdatedAt = now;
    }

    public void ChangePasswordHash(string passwordHash, DateTimeOffset now)
    {
        PasswordHash = passwordHash;
        UpdatedAt = now;
    }
}
