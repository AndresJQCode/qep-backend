namespace Modules.Identity.Domain;

/// <summary>
/// Internal identity of an authenticatable person. Per ADR 0015 a user exists only
/// after being invited to a tenant (invitation-only provisioning); external logins
/// never auto-create users. Identity owns the user, its status and its provider
/// links; it does not own membership or evaluate authorization.
/// </summary>
public sealed class User
{
    private readonly List<ProviderLink> _providerLinks = [];

    private User()
    {
    }

    private User(UserId id, string email, UserStatus status, DateTimeOffset createdAt)
    {
        Id = id;
        Email = NormalizeEmail(email);
        Status = status;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public UserId Id { get; private set; }

    public string Email { get; private set; } = string.Empty;

    public UserStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyCollection<ProviderLink> ProviderLinks => _providerLinks.AsReadOnly();

    /// <summary>
    /// Creates a user in <see cref="UserStatus.Invited"/>. This is the only entry
    /// point for a new user; activation happens on the first successful external
    /// login with a matching verified email.
    /// </summary>
    public static User CreateInvited(UserId id, string email, DateTimeOffset createdAt) =>
        new(id, email, UserStatus.Invited, createdAt);

    /// <summary>
    /// Transitions an invited user to <see cref="UserStatus.Active"/>. Idempotent for
    /// an already-active user.
    /// </summary>
    public void Activate(DateTimeOffset occurredAt)
    {
        if (Status == UserStatus.Active)
        {
            return;
        }

        Status = UserStatus.Active;
        UpdatedAt = occurredAt;
    }

    /// <summary>
    /// Links an external provider subject to this user. Per ADR 0015 the caller must
    /// have already proven ownership of the email (Google <c>email_verified</c>).
    /// Idempotent: re-linking the same (provider, subject) is a no-op; a different
    /// subject for the same provider is rejected.
    /// </summary>
    public ProviderLink LinkProvider(string provider, string subject, DateTimeOffset occurredAt)
    {
        var normalizedProvider = NormalizeProvider(provider);
        var normalizedSubject = ValidateSubject(subject);

        var existing = _providerLinks.SingleOrDefault(link =>
            StringComparer.Ordinal.Equals(link.Provider, normalizedProvider));
        if (existing is not null)
        {
            if (!StringComparer.Ordinal.Equals(existing.Subject, normalizedSubject))
            {
                throw new IdentityDomainException(
                    "identity.provider_link.conflict",
                    "The user is already linked to a different subject for this provider.");
            }

            return existing;
        }

        var link = ProviderLink.Create(Id, normalizedProvider, normalizedSubject, occurredAt);
        _providerLinks.Add(link);
        UpdatedAt = occurredAt;
        return link;
    }

    public static string NormalizeEmail(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length is < 3 or > 254 ||
            normalized.Count(character => character == '@') != 1 ||
            normalized.StartsWith('@') ||
            normalized.EndsWith('@'))
        {
            throw new IdentityDomainException(
                "identity.email.invalid",
                "Email must be a single valid address.");
        }

        return normalized;
    }

    private static string NormalizeProvider(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length is 0 or > 50)
        {
            throw new IdentityDomainException(
                "identity.provider.invalid",
                "Provider must be a non-empty identifier of at most 50 characters.");
        }

        return normalized;
    }

    private static string ValidateSubject(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length is 0 or > 255)
        {
            throw new IdentityDomainException(
                "identity.subject.invalid",
                "Provider subject must be a non-empty value of at most 255 characters.");
        }

        return normalized;
    }
}
