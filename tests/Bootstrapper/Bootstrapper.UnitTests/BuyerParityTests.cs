using FluentValidation.Results;
using Modules.Buyers.Api;
using Modules.Buyers.Application;
using Modules.Buyers.Domain;
using Modules.Buyers.Infrastructure.Signup;
using Modules.Identity.Application;
using Modules.Store.Api;
using Modules.Store.Application;
using Modules.Store.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// El perfil del comprador se guarda para precargar el formulario del pedido: si sus reglas se alejan de las del pedido,
/// un perfil guardado dejaría de poder enviarse. Este proyecto ve los tres módulos y los compara.
/// </summary>
public sealed class BuyerParityTests
{
    private static readonly Guid Department = Guid.CreateVersion7();
    private static readonly Guid City = Guid.CreateVersion7();

    [Fact]
    public void ThePasswordLimitsMatchIdentity()
    {
        Assert.Equal(PasswordRules.MinLength, BuyerPasswordRules.MinLength);
        Assert.Equal(PasswordRules.MaxLength, BuyerPasswordRules.MaxLength);
    }

    [Fact]
    public void TheSignupEventContractMatches()
    {
        Assert.Equal(StoreIntegrationEvents.BuyerSignupRequested, BuyerSignupRequestedMessage.EventName);

        var billing = new PublicBillingInput("Ana", "CC", "123", "3001234567", "ana@correo.com", "Calle 1", "CO", Department, City, null);
        var shipping = new PublicShippingInput("Luis", "3009876543", "Carrera 4", "ES", null, null, "Madrid");
        var payload = new StoreBuyerSignupRequestedPayload(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "acme.lulocrm.com", "Café <Origen>",
            "203.0.113.9", "ana@correo.com", "AQAAAAIAAYagAAAAE-hash", billing, shipping);

        var message = BuyerSignupRequestedMessage.Parse(System.Text.Json.JsonSerializer.Serialize(payload, System.Text.Json.JsonSerializerOptions.Web));

        Assert.Equal(payload.OrderRequestId, message.OrderRequestId);
        Assert.Equal(payload.TenantId, message.TenantId);
        Assert.Equal(payload.ClientRequestId, message.ClientRequestId);
        Assert.Equal(payload.StoreHost, message.StoreHost);
        Assert.Equal(payload.StoreName, message.StoreName);
        Assert.Equal(payload.VisitorIp, message.VisitorIp);
        Assert.Equal(payload.Email, message.Email);
        Assert.Equal(payload.PasswordHash, message.PasswordHash);
        Assert.Equal(
            new BuyerBillingDto(billing.Name, billing.IdentificationType, billing.IdentificationNumber, billing.Phone, billing.Email,
                billing.Address, billing.Country, billing.DepartmentId, billing.CityId, billing.CityName),
            message.Billing);
        Assert.Equal(
            new BuyerShippingDto(shipping.RecipientName, shipping.Phone, shipping.Address, shipping.Country, shipping.DepartmentId,
                shipping.CityId, shipping.CityName),
            message.Shipping);

        // Un campo nuevo de un lado que el otro no lee rompería el contrato en silencio: los nombres tienen que ser los mismos.
        Assert.Equal(PropertyNames(typeof(StoreBuyerSignupRequestedPayload)), PropertyNames(typeof(BuyerSignupRequestedMessage)));
        Assert.Equal(PropertyNames(typeof(PublicBillingInput)), PropertyNames(typeof(BuyerBillingDto)));
        Assert.Equal(PropertyNames(typeof(PublicShippingInput)), PropertyNames(typeof(BuyerShippingDto)));
    }

    private static string[] PropertyNames(Type type) =>
        type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(property => property.Name != "EqualityContract")
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

    [Fact]
    public void StoreCreateAccountLimitsMatchBuyers()
    {
        Assert.Equal(BuyerPasswordRules.MinLength, PublicOrderRequestBodyValidator.BuyerPasswordMinLength);
        Assert.Equal(BuyerPasswordRules.MaxLength, PublicOrderRequestBodyValidator.BuyerPasswordMaxLength);
    }

    [Fact]
    public void TheBuyerSessionHeaderMatches() =>
        Assert.Equal(PublicBuyerEndpoints.BuyerSessionHeader, StorefrontClient.BuyerSessionHeader);

    [Fact]
    public void TheProfileLimitsMatchTheStoreConstants()
    {
        Assert.Equal(StoreRequestBilling.NameMaxLength, BuyerProfileValidator.NameMaxLength);
        Assert.Equal(StoreRequestBilling.IdentificationNumberMaxLength, BuyerProfileValidator.IdentificationNumberMaxLength);
        Assert.Equal(StoreRequestBilling.EmailMaxLength, BuyerProfileValidator.EmailMaxLength);
        Assert.Equal(StoreRequestShipping.RecipientNameMaxLength, BuyerProfileValidator.RecipientNameMaxLength);
        Assert.Equal(StorePlace.NameMaxLength, BuyerProfileValidator.CityNameMaxLength);
        Assert.Equal(PublicOrderRequestBodyValidator.PhoneMaxLength, BuyerProfileValidator.PhoneMaxLength);
        Assert.Equal(PublicOrderRequestBodyValidator.AddressMaxLength, BuyerProfileValidator.AddressMaxLength);
    }

    public static TheoryData<string?> Emails => new()
    {
        "ana@correo.com",
        "  Ana.Perez@Correo.COM ",
        "sin-arroba",
        "",
        "   ",
        null!,
        new string('a', 245) + "@correo.co",
        new string('a', 244) + "@correo.co",
    };

    [Theory]
    [MemberData(nameof(Emails))]
    public void TheCheckoutEmailNormalizationMatchesBuyers(string? raw)
    {
        var buyers = BuyerEmail.TryNormalize(raw, out var expected);
        var store = StoreBuyerSignup.TryNormalizeEmail(raw, out var actual);

        Assert.Equal(buyers, store);
        Assert.Equal(expected, actual);
    }

    private static BuyerBillingDto Co() =>
        new("Ana", "CC", "123", "3001234567", "ana@correo.com", "Calle 1", "CO", Department, City, null);

    public static TheoryData<string, BuyerBillingDto> BillingCases => new()
    {
        { "valid CO", Co() },
        { "valid ES with city name", Co() with { Country = "ES", DepartmentId = null, CityId = null, CityName = "Madrid" } },
        { "no city", Co() with { Country = "ES", DepartmentId = null, CityId = null, CityName = null } },
        { "type XX", Co() with { IdentificationType = "XX" } },
        { "CO without department", Co() with { DepartmentId = null } },
        { "name of 201", Co() with { Name = new string('a', 201) } },
        { "invalid email", Co() with { Email = "no-es-un-correo" } },
        { "country COL", Co() with { Country = "COL" } },
    };

    private static BuyerShippingDto ShipCo() =>
        new("Luis", "3009876543", "Carrera 4", "CO", Department, City, null);

    public static TheoryData<string, BuyerShippingDto> ShippingCases => new()
    {
        { "valid CO", ShipCo() },
        { "valid ES with city name", ShipCo() with { Country = "ES", DepartmentId = null, CityId = null, CityName = "Madrid" } },
        { "no city", ShipCo() with { Country = "ES", DepartmentId = null, CityId = null, CityName = null } },
        { "CO without department", ShipCo() with { DepartmentId = null } },
        { "recipient of 121", ShipCo() with { RecipientName = new string('a', 121) } },
        { "empty phone", ShipCo() with { Phone = "" } },
        { "address of 201", ShipCo() with { Address = new string('a', 201) } },
        { "country COL", ShipCo() with { Country = "COL" } },
    };

    [Theory]
    [MemberData(nameof(BillingCases))]
    public void TheProfileRulesMatchTheOrderRulesForBilling(string label, BuyerBillingDto billing)
    {
        var profile = new BuyerProfileDto(billing, null, true);
        var order = Order(
            new PublicBillingInput(
                billing.Name, billing.IdentificationType, billing.IdentificationNumber, billing.Phone, billing.Email,
                billing.Address, billing.Country, billing.DepartmentId, billing.CityId, billing.CityName),
            null);

        AssertSameVerdict(label, new BuyerProfileValidator().Validate(profile), new PublicOrderRequestBodyValidator().Validate(order), "Billing");
    }

    [Theory]
    [MemberData(nameof(ShippingCases))]
    public void TheProfileRulesMatchTheOrderRulesForShipping(string label, BuyerShippingDto shipping)
    {
        var profile = new BuyerProfileDto(Co(), shipping, false);
        var billing = Co();
        var order = Order(
            new PublicBillingInput(
                billing.Name, billing.IdentificationType, billing.IdentificationNumber, billing.Phone, billing.Email,
                billing.Address, billing.Country, billing.DepartmentId, billing.CityId, billing.CityName),
            new PublicShippingInput(
                shipping.RecipientName, shipping.Phone, shipping.Address, shipping.Country, shipping.DepartmentId,
                shipping.CityId, shipping.CityName));

        AssertSameVerdict(label, new BuyerProfileValidator().Validate(profile), new PublicOrderRequestBodyValidator().Validate(order), "Shipping");
    }

    private static PublicOrderRequestBody Order(PublicBillingInput billing, PublicShippingInput? shipping) =>
        new(Guid.CreateVersion7(), "token", "COP", billing, shipping, null, true, [new PublicCartLineRequest(Guid.CreateVersion7(), 1)]);

    private static void AssertSameVerdict(string label, ValidationResult profile, ValidationResult order, string block)
    {
        var profileFields = profile.Errors.Where(e => e.PropertyName.StartsWith(block, StringComparison.Ordinal))
            .Select(e => e.PropertyName).Distinct().Order().ToArray();
        var orderFields = order.Errors.Where(e => e.PropertyName.StartsWith(block, StringComparison.Ordinal))
            .Select(e => e.PropertyName).Distinct().Order().ToArray();

        Assert.True(profileFields.SequenceEqual(orderFields), $"{label}: profile [{string.Join(", ", profileFields)}] vs order [{string.Join(", ", orderFields)}]");
    }
}
