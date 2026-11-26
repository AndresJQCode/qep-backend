namespace Modules.Buyers.Domain;

public sealed class BuyerConsent
{
    private BuyerConsent()
    {
    }

    public Guid Id { get; private set; }
    public Guid AccountId { get; private set; }
    public string Email { get; private set; } = string.Empty;
    public DateTimeOffset AcceptedAt { get; private set; }
    public string VisitorIp { get; private set; } = string.Empty;
    public string StoreHost { get; private set; } = string.Empty;
    public string PolicyUrl { get; private set; } = string.Empty;
    public string PolicyVersion { get; private set; } = string.Empty;

    public static BuyerConsent Record(
        Guid id, Guid accountId, string email, string visitorIp, string storeHost, DateTimeOffset now) =>
        new()
        {
            Id = id,
            AccountId = accountId,
            Email = email,
            AcceptedAt = now,
            VisitorIp = visitorIp,
            StoreHost = storeHost,
            PolicyUrl = BuyerDataPolicy.UrlFor(storeHost),
            PolicyVersion = BuyerDataPolicy.Version,
        };
}
