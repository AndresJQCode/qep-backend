using Modules.Buyers.Application;

namespace Modules.Buyers.UnitTests;

public sealed class BuyerProfileValidatorTests
{
    private static readonly BuyerProfileValidator Validator = new();

    private static BuyerBillingDto Billing() => new(
        "Ana Pérez", "CC", "123456", "3001234567", "ana@correo.com", "Calle 1 # 2-3", "CO",
        Guid.CreateVersion7(), Guid.CreateVersion7(), null);

    private static BuyerShippingDto Shipping() => new(
        "Luis", "3009876543", "Carrera 4 # 5-6", "CO", Guid.CreateVersion7(), Guid.CreateVersion7(), null);

    [Fact]
    public void AValidColombianProfilePasses() =>
        Assert.True(Validator.Validate(new BuyerProfileDto(Billing(), null, true)).IsValid);

    [Fact]
    public void AValidProfileWithItsOwnShippingPasses() =>
        Assert.True(Validator.Validate(new BuyerProfileDto(Billing(), Shipping(), false)).IsValid);

    [Fact]
    public void ShippingSameAsBillingWithAShippingBlockFails()
    {
        var result = Validator.Validate(new BuyerProfileDto(Billing(), Shipping(), true));

        Assert.Contains(result.Errors, e => e.PropertyName == "Shipping");
    }

    [Fact]
    public void NoShippingWithShippingSameAsBillingFalseFails()
    {
        var result = Validator.Validate(new BuyerProfileDto(Billing(), null, false));

        Assert.Contains(result.Errors, e => e.PropertyName == "Shipping");
    }

    [Fact]
    public void AnUnknownIdentificationTypeFails()
    {
        var result = Validator.Validate(new BuyerProfileDto(Billing() with { IdentificationType = "XX" }, null, true));

        Assert.Contains(result.Errors, e => e.PropertyName == "Billing.IdentificationType");
    }

    [Fact]
    public void ColombiaWithOnlyTheCityIdFails()
    {
        var result = Validator.Validate(new BuyerProfileDto(Billing() with { DepartmentId = null }, null, true));

        Assert.Contains(result.Errors, e => e.PropertyName == "Billing.CityId");
    }
}
