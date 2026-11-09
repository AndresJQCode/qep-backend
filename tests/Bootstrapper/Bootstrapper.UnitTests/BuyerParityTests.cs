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

    public static TheoryData<string, BuyerBillingDto, bool> BillingCases => new()
    {
        { "valid CO", Co(), true },
        { "valid ES with city name", Co() with { IdentificationType = "PASAPORTE", Country = "ES", DepartmentId = null, CityId = null, CityName = "Madrid" }, true },
        { "valid ES with NIF", Co() with { IdentificationType = "NIF", Country = "ES", DepartmentId = null, CityId = null, CityName = "Madrid" }, true },
        { "valid US with TAX_ID", Co() with { IdentificationType = "TAX_ID", Country = "US", DepartmentId = null, CityId = null, CityName = "Miami" }, true },
        { "ES with CC", Co() with { Country = "ES", DepartmentId = null, CityId = null, CityName = "Madrid" }, false },
        { "CO with NIF", Co() with { IdentificationType = "NIF" }, false },
        { "lowercase nif with es", Co() with { IdentificationType = " nif ", Country = "es", DepartmentId = null, CityId = null, CityName = "Madrid" }, true },
        { "lowercase codes", Co() with { IdentificationType = " cc ", Country = "co" }, true },
        { "no city", Co() with { IdentificationType = "PASAPORTE", Country = "ES", DepartmentId = null, CityId = null, CityName = null }, false },
        { "type XX", Co() with { IdentificationType = "XX" }, false },
        { "CO without department", Co() with { DepartmentId = null }, false },
        { "name of 201", Co() with { Name = new string('a', 201) }, false },
        { "invalid email", Co() with { Email = "no-es-un-correo" }, false },
        { "country COL", Co() with { Country = "COL" }, false },
    };

    private static BuyerShippingDto ShipCo() =>
        new("Luis", "3009876543", "Carrera 4", "CO", Department, City, null);

    public static TheoryData<string, BuyerShippingDto, bool> ShippingCases => new()
    {
        { "valid CO", ShipCo(), true },
        { "valid ES with city name", ShipCo() with { Country = "ES", DepartmentId = null, CityId = null, CityName = "Madrid" }, true },
        { "lowercase country", ShipCo() with { Country = " co " }, true },
        { "no city", ShipCo() with { Country = "ES", DepartmentId = null, CityId = null, CityName = null }, false },
        { "CO without department", ShipCo() with { DepartmentId = null }, false },
        { "recipient of 121", ShipCo() with { RecipientName = new string('a', 121) }, false },
        { "empty phone", ShipCo() with { Phone = "" }, false },
        { "address of 201", ShipCo() with { Address = new string('a', 201) }, false },
        { "country COL", ShipCo() with { Country = "COL" }, false },
    };

    [Theory]
    [MemberData(nameof(BillingCases))]
    public void TheProfileRulesMatchTheOrderRulesForBilling(string label, BuyerBillingDto billing, bool expectValid)
    {
        var profile = new BuyerProfileDto(billing, null, true);
        var order = Order(
            new PublicBillingInput(
                billing.Name, billing.IdentificationType, billing.IdentificationNumber, billing.Phone, billing.Email,
                billing.Address, billing.Country, billing.DepartmentId, billing.CityId, billing.CityName),
            null);

        AssertSameVerdict(
            label, new BuyerProfileValidator().Validate(profile), new PublicOrderRequestBodyValidator().Validate(order), "Billing", expectValid);
        AssertNormalizedProfileVerdict(label, profile, "Billing", expectValid);
    }

    [Theory]
    [MemberData(nameof(ShippingCases))]
    public void TheProfileRulesMatchTheOrderRulesForShipping(string label, BuyerShippingDto shipping, bool expectValid)
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

        AssertSameVerdict(
            label, new BuyerProfileValidator().Validate(profile), new PublicOrderRequestBodyValidator().Validate(order), "Shipping", expectValid);
        AssertNormalizedProfileVerdict(label, profile, "Shipping", expectValid);
    }

    private static PublicOrderRequestBody Order(PublicBillingInput billing, PublicShippingInput? shipping) =>
        new(Guid.CreateVersion7(), "token", "COP", billing, shipping, null, true, [new PublicCartLineRequest(Guid.CreateVersion7(), 1)]);

    private static string[] FieldsWithErrors(ValidationResult result, string block) =>
        result.Errors.Where(e => e.PropertyName.StartsWith(block, StringComparison.Ordinal))
            .Select(e => e.PropertyName).Distinct().Order().ToArray();

    // Mismos campos con error de los dos lados, y además el veredicto esperado: sin él, una regla que se cae de los dos lados a la
    // vez (los dos aceptan lo inválido, o los dos rechazan lo válido) pasaría como paridad.
    private static void AssertSameVerdict(string label, ValidationResult profile, ValidationResult order, string block, bool expectValid)
    {
        var profileFields = FieldsWithErrors(profile, block);
        var orderFields = FieldsWithErrors(order, block);

        Assert.True(profileFields.SequenceEqual(orderFields), $"{label}: profile [{string.Join(", ", profileFields)}] vs order [{string.Join(", ", orderFields)}]");
        if (expectValid)
        {
            Assert.True(profileFields.Length == 0, $"{label}: expected valid, profile has [{string.Join(", ", profileFields)}]");
            Assert.True(orderFields.Length == 0, $"{label}: expected valid, order has [{string.Join(", ", orderFields)}]");
        }
        else
        {
            Assert.True(profileFields.Length > 0, $"{label}: expected errors in the profile");
            Assert.True(orderFields.Length > 0, $"{label}: expected errors in the order");
        }
    }

    // Lo que guarda BuyerProfileWriter es el perfil con los códigos normalizados: un perfil que el pedido acepta tiene que seguir
    // pasando el validador del perfil después de normalizar, para que precargado se pueda volver a enviar.
    private static void AssertNormalizedProfileVerdict(string label, BuyerProfileDto profile, string block, bool expectValid)
    {
        var fields = FieldsWithErrors(new BuyerProfileValidator().Validate(profile.WithNormalizedCodes()), block);
        Assert.True(expectValid == (fields.Length == 0), $"{label}: normalized profile [{string.Join(", ", fields)}]");
    }
}
