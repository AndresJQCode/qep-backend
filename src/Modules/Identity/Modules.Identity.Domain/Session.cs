namespace Modules.Identity.Domain;

/// <summary>
/// A server-side authenticated session, identified to the browser by an opaque
/// httpOnly cookie. Only <see cref="TokenHash"/> (never the raw token) is persisted,
/// so a database read cannot be used to replay a session. Authentication only —
/// tenant/permission context is resolved live per request from membership state, not
/// carried on the session (see <c>ExternalClaimsTransformation</c>).
/// </summary>
public sealed class Session
{
    private Session()
    {
    }

    private Session(
        SessionId id,
        UserId userId,
        string tokenHash,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        string? userAgent,
        string? ipAddress)
    {
        Id = id;
        UserId = userId;
        TokenHash = tokenHash;
        CreatedAt = createdAt;
        LastSeenAt = createdAt;
        ExpiresAt = expiresAt;
        UserAgent = userAgent;
        IpAddress = ipAddress;
    }

    public SessionId Id { get; private set; }

    public UserId UserId { get; private set; }

    public string TokenHash { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset LastSeenAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public string? RevokedReason { get; private set; }

    public string? UserAgent { get; private set; }

    public string? IpAddress { get; private set; }

    public static Session Issue(
        SessionId id,
        UserId userId,
        string tokenHash,
        DateTimeOffset issuedAt,
        TimeSpan absoluteLifetime,
        string? userAgent,
        string? ipAddress)
    {
        if (string.IsNullOrWhiteSpace(tokenHash))
        {
            throw new IdentityDomainException(
                "identity.session.token_hash_required",
                "Token hash must not be empty.");
        }

        return new Session(
            id,
            userId,
            tokenHash,
            issuedAt,
            issuedAt + absoluteLifetime,
            Truncate(userAgent, 300),
            Truncate(ipAddress, 45));
    }

    /// <summary>
    /// Bumps <see cref="LastSeenAt"/>. Callers should throttle this (e.g. only when
    /// stale by more than a few minutes) so idle tracking does not turn every
    /// authenticated request into a write.
    /// </summary>
    public void Touch(DateTimeOffset now)
    {
        LastSeenAt = now;
    }

    public void Revoke(DateTimeOffset now, string reason)
    {
        if (RevokedAt is not null)
        {
            return;
        }

        RevokedAt = now;
        RevokedReason = reason;
    }

    public bool IsValid(DateTimeOffset now, TimeSpan idleTimeout) =>
        RevokedAt is null
        && now < ExpiresAt
        && now - LastSeenAt < idleTimeout;

    private static string? Truncate(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Length > maxLength ? value[..maxLength] : value;
}
