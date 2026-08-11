namespace Modules.Identity.Domain;

/// <summary>
/// Una sesión autenticada del lado del servidor, identificada ante el navegador por una
/// cookie httpOnly opaca. Sólo se persiste <see cref="TokenHash"/> (nunca el token crudo),
/// así que una lectura de la base no sirve para reproducir una sesión. Es sólo
/// autenticación — el contexto de tenant/permisos se resuelve en vivo por request desde el
/// estado de la membresía, no viaja en la sesión (ver <c>ExternalClaimsTransformation</c>).
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
    /// Empuja <see cref="LastSeenAt"/>. Los llamadores deberían limitar esto (por ejemplo sólo
    /// cuando esté viejo por más de unos minutos) para que el seguimiento de inactividad no
    /// convierta cada request autenticado en una escritura.
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
