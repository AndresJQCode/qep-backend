namespace Modules.Buyers.Domain;

public sealed class BuyerSession
{
    public static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromDays(30);
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromDays(7);
    public static readonly TimeSpan TouchThreshold = TimeSpan.FromMinutes(5);

    private const int MaxIpLength = 45;

    private BuyerSession()
    {
    }

    public Guid Id { get; private set; }
    public Guid AccountId { get; private set; }
    public string StoreHost { get; private set; } = string.Empty;
    public string TokenHash { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset LastSeenAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public string? RevokedReason { get; private set; }
    public string? IpAddress { get; private set; }

    public static BuyerSession Issue(
        Guid id, Guid accountId, string storeHost, string tokenHash, DateTimeOffset now, string? ipAddress) =>
        new()
        {
            Id = id,
            AccountId = accountId,
            StoreHost = storeHost,
            TokenHash = tokenHash,
            CreatedAt = now,
            LastSeenAt = now,
            ExpiresAt = now + AbsoluteLifetime,
            IpAddress = ipAddress is { Length: > MaxIpLength } ? ipAddress[..MaxIpLength] : ipAddress,
        };

    public bool IsValidFor(string storeHost, DateTimeOffset now) =>
        RevokedAt is null
        && string.Equals(StoreHost, storeHost, StringComparison.Ordinal)
        && now < ExpiresAt
        && now - LastSeenAt < IdleTimeout;

    /// <summary>Avanza <see cref="LastSeenAt"/> sólo si pasó el umbral; devuelve si hubo escritura.</summary>
    public bool Touch(DateTimeOffset now)
    {
        if (now - LastSeenAt < TouchThreshold) return false;

        LastSeenAt = now;
        return true;
    }

    public void Revoke(DateTimeOffset now, string reason)
    {
        if (RevokedAt is not null) return;

        RevokedAt = now;
        RevokedReason = reason;
    }
}
