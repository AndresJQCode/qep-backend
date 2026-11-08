using Modules.Buyers.Domain;

namespace Modules.Buyers.Application;

/// <param name="CityName">Sólo fuera de Colombia; con <c>CO</c> van los ids de DIVIPOLA.</param>
public sealed record BuyerBillingDto(
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

public sealed record BuyerShippingDto(
    string? RecipientName,
    string Phone,
    string Address,
    string Country,
    Guid? DepartmentId,
    Guid? CityId,
    string? CityName);

public sealed record BuyerProfileDto(BuyerBillingDto Billing, BuyerShippingDto? Shipping, bool ShippingSameAsBilling);

public sealed record BuyerDto(string Email, BuyerProfileDto? Profile);

/// <param name="SessionToken">El token opaco en claro: sólo se devuelve aquí, en la respuesta que lo crea.</param>
public sealed record BuyerSessionDto(string SessionToken, DateTimeOffset ExpiresAt, BuyerDto Buyer);

public static class BuyerProfileMapping
{
    public static BuyerProfileData ToData(this BuyerProfileDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        var b = dto.Billing;
        var s = dto.Shipping;
        return new BuyerProfileData(
            new BuyerBillingData(
                b.Name, b.IdentificationType, b.IdentificationNumber, b.Phone, b.Email, b.Address, b.Country,
                b.DepartmentId, b.CityId, b.CityName),
            s is null
                ? null
                : new BuyerShippingData(s.RecipientName, s.Phone, s.Address, s.Country, s.DepartmentId, s.CityId, s.CityName),
            dto.ShippingSameAsBilling);
    }

    /// <summary>
    /// Los códigos del perfil como los guarda la consola (tipo de documento y país en mayúsculas, sin espacios). El pedido los acepta
    /// sin mirar mayúsculas y el perfil también, así que <c>BuyerProfileWriter</c> pasa todo perfil por aquí antes de guardarlo.
    /// </summary>
    public static BuyerProfileDto WithNormalizedCodes(this BuyerProfileDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        return dto with
        {
            Billing = dto.Billing with
            {
                IdentificationType = dto.Billing.IdentificationType.Trim().ToUpperInvariant(),
                Country = dto.Billing.Country.Trim().ToUpperInvariant(),
            },
            Shipping = dto.Shipping is null ? null : dto.Shipping with { Country = dto.Shipping.Country.Trim().ToUpperInvariant() },
        };
    }

    public static BuyerProfileDto ToDto(this BuyerProfileData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var b = data.Billing;
        var s = data.Shipping;
        return new BuyerProfileDto(
            new BuyerBillingDto(
                b.Name, b.IdentificationType, b.IdentificationNumber, b.Phone, b.Email, b.Address, b.Country,
                b.DepartmentId, b.CityId, b.CityName),
            s is null
                ? null
                : new BuyerShippingDto(s.RecipientName, s.Phone, s.Address, s.Country, s.DepartmentId, s.CityId, s.CityName),
            data.ShippingSameAsBilling);
    }
}
