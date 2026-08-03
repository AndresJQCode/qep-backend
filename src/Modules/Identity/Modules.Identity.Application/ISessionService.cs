namespace Modules.Identity.Application;

public sealed record SessionIssueResult(string RawToken, DateTimeOffset ExpiresAt);

public sealed record SessionPrincipal(Guid UserId);

/// <summary>
/// Issues, validates and revokes opaque-token sessions backed by the
/// <c>identity.sessions</c> table. The raw token is returned to the caller exactly
/// once, at issuance, and is never persisted — only its hash is stored, so a
/// database read cannot be replayed as a valid session (see <c>Session</c>).
/// </summary>
public interface ISessionService
{
    Task<SessionIssueResult> IssueAsync(
        Guid userId,
        string? userAgent,
        string? ipAddress,
        CancellationToken cancellationToken);

    /// <returns>The session's principal, or <c>null</c> if the token is unknown,
    /// expired, idle-timed-out or revoked.</returns>
    Task<SessionPrincipal?> ValidateAsync(string rawToken, CancellationToken cancellationToken);

    Task RevokeAsync(string rawToken, string reason, CancellationToken cancellationToken);

    /// <returns>The number of sessions revoked.</returns>
    Task<int> RevokeAllForUserAsync(
        Guid userId,
        string reason,
        CancellationToken cancellationToken);
}
