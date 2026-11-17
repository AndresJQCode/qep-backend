using Modules.Customers.Application;

namespace Modules.Customers.UnitTests;

/// <summary>La regla «tipo de documento permitido para el país» del validador de alta.</summary>
public sealed class CustomerDocumentCountryRulesTests
{
    private static readonly CreateCustomerValidator Validator = new();

    private static CreateCustomerCommand Command(string type, string country) =>
        new(CustomerWriteDoubles.Tenant, "Laura Pérez", null, type, "X1234567", "310 935 2187", "laura@verde.co",
            "Calle 10", country,
            country.Trim().Equals("CO", StringComparison.OrdinalIgnoreCase) ? CustomerWriteDoubles.CityId : null,
            country.Trim().Equals("CO", StringComparison.OrdinalIgnoreCase) ? null : "Madrid", null, false);

    [Theory]
    [InlineData("NIF", "ES")]
    [InlineData("NIE", "ES")]
    [InlineData("TAX_ID", "US")]
    [InlineData("CC", "CO")]
    [InlineData("PASAPORTE", "ES")]
    [InlineData(" nif ", "es")]
    public void AnAllowedPairIsValid(string type, string country) =>
        Assert.True(Validator.Validate(Command(type, country)).IsValid);

    [Theory]
    [InlineData("NIF", "CO")]
    [InlineData("CC", "ES")]
    [InlineData("NIT", "US")]
    [InlineData("TAX_ID", "CO")]
    public void ATypeTheCountryDoesNotAllowFailsOnTheTypeOnly(string type, string country)
    {
        var errors = Validator.Validate(Command(type, country)).Errors;

        Assert.Equal(["IdentificationType"], errors.Select(error => error.PropertyName).Distinct());
        Assert.Equal("The identification type is not allowed for the country.", Assert.Single(errors).ErrorMessage);
    }

    [Fact]
    public void AMalformedCountryIsReportedOnTheCountryAlone()
    {
        var fields = Validator.Validate(Command("NIF", "COL")).Errors.Select(error => error.PropertyName).ToList();

        Assert.Contains("Country", fields);
        Assert.DoesNotContain("IdentificationType", fields);
    }

    [Fact]
    public void AnUnknownTypeKeepsTheListMessage() =>
        Assert.Contains(
            "NIF",
            Assert.Single(
                Validator.Validate(Command("XX", "CO")).Errors,
                error => error.PropertyName == "IdentificationType").ErrorMessage);

    [Theory]
    [InlineData(null, null)]
    [InlineData("  ", "  ")]
    public void BothDocumentFieldsEmptyIsNotAValidatorError(string? type, string? number)
    {
        var fields = Validator.Validate(Command("CC", "CO") with { IdentificationType = type, IdentificationNumber = number })
            .Errors.Select(error => error.PropertyName).ToList();

        Assert.DoesNotContain("IdentificationType", fields);
        Assert.DoesNotContain("IdentificationNumber", fields);
    }

    [Fact]
    public void AColombianBodyWithoutCityIdIsNotAValidatorError() =>
        Assert.DoesNotContain(
            "CityId",
            Validator.Validate(Command("CC", "CO") with { CityId = null }).Errors.Select(error => error.PropertyName));

    [Fact]
    public void AnEmptyCityIdIsStillAValidatorError() =>
        Assert.Contains(
            "CityId",
            Validator.Validate(Command("CC", "CO") with { CityId = Guid.Empty }).Errors.Select(error => error.PropertyName));
}
