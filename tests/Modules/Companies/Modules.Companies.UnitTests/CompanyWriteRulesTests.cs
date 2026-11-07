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
            .Validate(CompanyLegacyWrite.Resolve(command))
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

    [Fact]
    public void LegacyCreateResolvesToColombiaAndNit()
    {
        var resolved = CompanyLegacyWrite.Resolve(Command(country: null, documentType: null));

        Assert.Equal("CO", resolved.Country);
        Assert.Equal("NIT", resolved.DocumentType);
        Assert.True(new CreateCompanyValidator().Validate(resolved).IsValid);
    }

    [Fact]
    public void LegacyUpdateUsesStoredSpanishCountry()
    {
        var stored = Company.Create(
            CompanyId.New(),
            Guid.NewGuid(),
            "Empresa",
            new List<CompanyBankAccountPayload> { new("Banco", "123", "EUR") }.ToDomain(),
            "A1234567",
            "ES",
            CompanyDocumentType.Nif,
            null,
            "Madrid",
            new CompanyContactInfo(),
            DateTimeOffset.UtcNow);
        var command = new UpdateCompanyCommand(
            stored.TenantId,
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

        var resolved = CompanyLegacyWrite.Resolve(command, stored);
        var result = new UpdateCompanyValidator().Validate(resolved);

        Assert.Equal("ES", resolved.Country);
        Assert.Contains(result.Errors, error => error.PropertyName == "CityName");
    }

    [Fact]
    public void PaddedLowerCaseCountryAcceptsNif() =>
        Assert.True(new CreateCompanyValidator()
            .Validate(CompanyLegacyWrite.Resolve(
                Command(country: " es ", documentType: "NIF", withCity: false, cityName: "Madrid")))
            .IsValid);
}
