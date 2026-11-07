namespace Modules.Buyers.Domain;

public enum BuyerEmailTokenPurpose
{
    Verify = 1,
    Reset = 2,
}

public sealed class BuyerEmailToken
{
    public static readonly TimeSpan VerifyLifetime = TimeSpan.FromHours(24);
    public static readonly TimeSpan ResetLifetime = TimeSpan.FromHours(1);

    private BuyerEmailToken()
    {
    }

    public Guid Id { get; private set; }
    public Guid AccountId { get; private set; }
    public BuyerEmailTokenPurpose Purpose { get; private set; }
    public string TokenHash { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? UsedAt { get; private set; }

    public static BuyerEmailToken Issue(
        Guid id, Guid accountId, BuyerEmailTokenPurpose purpose, string tokenHash, DateTimeOffset now) =>
        new()
        {
            Id = id,
            AccountId = accountId,
            Purpose = purpose,
            TokenHash = tokenHash,
            CreatedAt = now,
            ExpiresAt = now + (purpose == BuyerEmailTokenPurpose.Verify ? VerifyLifetime : ResetLifetime),
        };
}
