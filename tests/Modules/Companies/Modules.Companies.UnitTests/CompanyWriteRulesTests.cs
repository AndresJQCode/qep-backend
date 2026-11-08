using Modules.Companies.Application;
using Modules.Companies.Domain;

namespace Modules.Companies.UnitTests;

/// <summary>Spec D6: the account currency decides the currency of every quotation billed to it,
/// so it must be a catalogue code — not just "three letters" as before. Spec §6.2: country,
/// document type and the city lane.</summary>
public sealed class CompanyWriteRulesTests
{
    private static readonly Guid SomeCity = Guid.NewGuid();

    private static CreateCompanyCommand Command(
        string currency = "COP",
        string? country = "CO",
        string? documentType = "NIT",
        Guid? cityId = null,
        string? cityName = null,
        bool withCity = true) =>
        new(
            Guid.NewGuid(),
            "QEP Comercial S.A.S.",
            [new CompanyBankAccountPayload("Bancolombia", "12345678", currency)],
            "901.123.456-2",
            country,
            documentType,
            cityId ?? (withCity ? SomeCity : null),
            cityName,
            null,
            null,
            null);

    private static bool Fails(CreateCompanyCommand command, string key) =>
        new CreateCompanyValidator()
            .Validate(command)
            .Errors.Any(error => error.PropertyName == key);

    [Theory]
    [InlineData("EUR")]
    [InlineData("cop")]
    public void ACatalogueCurrencyIsValid(string currency)
    {
        var result = new CreateCompanyValidator().Validate(Command(currency));

        Assert.DoesNotContain(result.Errors, error => error.PropertyName == "BankAccounts[0].Currency");
    }

    [Theory]
    [InlineData("MXN")]
    [InlineData("ABC")]
    public void ACurrencyOutsideTheCatalogueFailsOnItsRow(string currency)
    {
        var result = new CreateCompanyValidator().Validate(Command(currency));

        Assert.Contains(result.Errors, error => error.PropertyName == "BankAccounts[0].Currency");
    }

    [Fact]
    public void ACountryThatIsNotTwoLettersFailsOnCountry() =>
        Assert.True(Fails(Command(country: "Col"), "Country"));

    [Fact]
    public void RutIsNotAllowedForSpain() =>
        Assert.True(Fails(Command(country: "ES", documentType: "RUT", withCity: false, cityName: "Madrid"), "DocumentType"));

    [Fact]
    public void NitIsNotAllowedForSpain() =>
        Assert.True(Fails(Command(country: "ES", documentType: "NIT", withCity: false, cityName: "Madrid"), "DocumentType"));

    [Fact]
    public void ColombiaWithoutACityIdFailsOnCityId() =>
        Assert.True(Fails(Command(withCity: false), "CityId"));

    [Fact]
    public void AForeignCountryWithoutACityNameFailsOnCityName() =>
        Assert.True(Fails(Command(country: "US", documentType: "TAX_ID", withCity: false), "CityName"));

    [Fact]
    public void ACityNameOver120CharactersFailsOnCityName() =>
        Assert.True(Fails(
            Command(country: "US", documentType: "TAX_ID", withCity: false, cityName: new string('a', 121)),
            "CityName"));

    [Fact]
    public void CountryWithoutDocumentTypeFailsOnDocumentType() =>
        Assert.True(Fails(Command(country: "CO", documentType: null), "DocumentType"));

    // Sin país ni tipo de documento ya no hay a qué resolver: la ventana del frontend viejo se
    // cerró el 2026-11-08 (decisión del owner) y los dos campos son obligatorios en POST y PUT.
    [Fact]
    public void CreateWithoutCountryFailsOnCountryAndDocumentType()
    {
        var result = new CreateCompanyValidator().Validate(Command(country: null, documentType: null));

        Assert.Contains(result.Errors, error => error.PropertyName == "Country");
        Assert.Contains(result.Errors, error => error.PropertyName == "DocumentType");
    }

    [Fact]
    public void CreateWithBlankCountryFailsOnCountry() =>
        Assert.True(Fails(Command(country: "  "), "Country"));

    [Fact]
    public void UpdateWithoutCountryFailsOnCountryAndDocumentType()
    {
        var command = new UpdateCompanyCommand(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Empresa",
            [new CompanyBankAccountPayload("Banco", "123", "EUR")],
            "A1234567",
            null,
            null,
            SomeCity,
            null,
            null,
            null,
            null);

        var result = new UpdateCompanyValidator().Validate(command);

        Assert.Contains(result.Errors, error => error.PropertyName == "Country");
        Assert.Contains(result.Errors, error => error.PropertyName == "DocumentType");
    }

    [Fact]
    public void PaddedLowerCaseCountryAcceptsNif() =>
        Assert.True(new CreateCompanyValidator()
            .Validate(Command(country: " es ", documentType: "NIF", withCity: false, cityName: "Madrid"))
            .IsValid);
}
