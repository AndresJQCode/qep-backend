namespace Modules.Buyers.Domain;

public sealed record BuyerBillingData(
    string Name,
    string IdentificationType,
    string IdentificationNumber,
    string Phone,
    string Email,
    string Address,
    string Country,
    Guid? DepartmentId,
    Guid? CityId,
    string? CityName);

public sealed record BuyerShippingData(
    string? RecipientName,
    string Phone,
    string Address,
    string Country,
    Guid? DepartmentId,
    Guid? CityId,
    string? CityName);

public sealed record BuyerProfileData(
    BuyerBillingData Billing,
    BuyerShippingData? Shipping,
    bool ShippingSameAsBilling);

public sealed class BuyerProfile
{
    private BuyerProfile()
    {
    }

    public Guid AccountId { get; private set; }
    public BuyerProfileData Data { get; private set; } = null!;
    public DateTimeOffset UpdatedAt { get; private set; }

    public static BuyerProfile Create(Guid accountId, BuyerProfileData data, DateTimeOffset now) =>
        new() { AccountId = accountId, Data = data, UpdatedAt = now };

    public void Replace(BuyerProfileData data, DateTimeOffset now)
    {
        Data = data;
        UpdatedAt = now;
    }
}
