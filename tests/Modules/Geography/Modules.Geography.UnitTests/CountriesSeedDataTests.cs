using Modules.Geography.Infrastructure.Seed;

namespace Modules.Geography.UnitTests;

/// <summary>Cruza countries.json (el recurso embebido que lee el arranque) con lo que promete el catálogo.</summary>
public sealed class CountriesSeedDataTests
{
    private static IReadOnlyList<CountryRecord> Load() =>
        DivipolaDataParser.ParseCountries(GeographySeeder.OpenResource(GeographySeeder.CountriesResourceSuffix));

    [Fact]
    public void TheCatalogHasTheFortyOneCountriesOfTheFrontend()
    {
        var countries = Load();

        Assert.Equal(41, countries.Count);
        Assert.Equal(41, countries.Select(country => country.Code).Distinct().Count());
        Assert.Contains(countries, country => country is { Code: "CO", Name: "Colombia" });
        Assert.Contains(countries, country => country is { Code: "VE", Name: "Venezuela" });
        Assert.Contains(countries, country => country is { Code: "PE", Name: "Perú" });
        Assert.Contains(countries, country => country is { Code: "US", Name: "Estados Unidos" });
    }

    [Fact]
    public void EveryCodeIsTwoUppercaseLetters() =>
        Assert.All(Load(), country =>
            Assert.True(country.Code is { Length: 2 } && country.Code.All(char.IsAsciiLetterUpper), country.Code));
}
