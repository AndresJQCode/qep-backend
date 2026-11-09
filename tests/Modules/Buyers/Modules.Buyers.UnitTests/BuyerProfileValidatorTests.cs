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

    private static List<string> Errors(BuyerBillingDto billing) =>
        Validator.Validate(new BuyerProfileDto(billing, null, true)).Errors.Select(error => error.PropertyName).ToList();

    // Colombia keeps its ids; any other country writes its city.
    private static BuyerBillingDto Abroad(string type, string country) =>
        country.Trim().Equals("CO", StringComparison.OrdinalIgnoreCase)
            ? Billing() with { IdentificationType = type, Country = country }
            : Billing() with { IdentificationType = type, Country = country, DepartmentId = null, CityId = null, CityName = "Madrid" };

    [Theory]
    [InlineData("NIF", "ES")]
    [InlineData("NIE", "ES")]
    [InlineData("TAX_ID", "US")]
    [InlineData(" nif ", " es ")]
    public void AnAllowedForeignPairIsValid(string type, string country) => Assert.Empty(Errors(Abroad(type, country)));

    [Fact]
    public void AColombianCcIsValid() => Assert.Empty(Errors(Billing()));

    [Theory]
    [InlineData("CC", "ES")]
    [InlineData("NIT", "US")]
    [InlineData("TAX_ID", "CO")]
    public void ATypeTheBillingCountryDoesNotAllowFailsOnTheTypeOnly(string type, string country) =>
        Assert.Equal(["Billing.IdentificationType"], Errors(Abroad(type, country)));

    [Fact]
    public void ANifInColombiaFailsWithTheCountryMessage()
    {
        var result = Validator.Validate(new BuyerProfileDto(Billing() with { IdentificationType = "NIF" }, null, true));
        Assert.Equal("El tipo de documento no es válido para el país.", Assert.Single(result.Errors).ErrorMessage);
    }

    [Fact]
    public void AMalformedCountryIsReportedOnTheCountryAlone()
    {
        var errors = Errors(Abroad("NIF", "COL"));
        Assert.Contains("Billing.Country", errors);
        Assert.DoesNotContain("Billing.IdentificationType", errors);
    }

    [Fact]
    public void AnUnknownTypeKeepsItsMessage()
    {
        var result = Validator.Validate(new BuyerProfileDto(Billing() with { IdentificationType = "XX" }, null, true));
        Assert.Equal("El tipo de documento no es válido.", Assert.Single(result.Errors).ErrorMessage);
    }

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
