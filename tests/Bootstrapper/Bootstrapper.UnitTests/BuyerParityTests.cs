using FluentValidation.Results;
using Modules.Buyers.Application;
using Modules.Identity.Application;
using Modules.Store.Application;

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
