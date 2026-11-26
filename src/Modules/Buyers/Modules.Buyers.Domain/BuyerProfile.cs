using BuildingBlocks.Domain.Identification;

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

    /// <summary>
    /// Si el tipo de documento del perfil es válido para su país de facturación. Sólo lo exigen <see cref="Create"/> y
    /// <see cref="Replace"/>: el record <see cref="BuyerBillingData"/> no lo comprueba, para que un jsonb viejo siga leyéndose.
    /// </summary>
    public static bool AcceptsDocument(BuyerBillingData billing)
    {
        ArgumentNullException.ThrowIfNull(billing);
        return DocumentTypesByCountry.IsAllowed(billing.IdentificationType, billing.Country);
    }

    public static BuyerProfile Create(Guid accountId, BuyerProfileData data, DateTimeOffset now)
    {
        EnsureDocumentAllowed(data);
        return new() { AccountId = accountId, Data = data, UpdatedAt = now };
    }

    public void Replace(BuyerProfileData data, DateTimeOffset now)
    {
        EnsureDocumentAllowed(data);
        Data = data;
        UpdatedAt = now;
    }

    private static void EnsureDocumentAllowed(BuyerProfileData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!AcceptsDocument(data.Billing))
        {
            throw new BuyerDomainException(BuyerErrorCodes.DocumentTypeNotAllowed, "El tipo de documento no es válido para el país.");
        }
    }
}
