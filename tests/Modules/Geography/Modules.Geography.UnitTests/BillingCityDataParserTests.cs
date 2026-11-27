using System.Text;
using Modules.Geography.Infrastructure.Seed;

namespace Modules.Geography.UnitTests;

public sealed class BillingCityDataParserTests
{
    private static MemoryStream Json(string json) => new(Encoding.UTF8.GetBytes(json));

    private static string Row(string country, string state, string city, string cityName = "Miami") =>
        $$"""{"countryCode":"{{country}}","countryName":"Estados Unidos","stateCode":"{{state}}","stateName":"Florida","cityCode":"{{city}}","cityName":"{{cityName}}"}""";

    [Fact]
    public void NamesAndCodesAreTrimmedIncludingNonBreakingSpaces()
    {
        var records = BillingCityDataParser.Parse(Json("[" + Row("Us", "09", "1", "\\u00A0Miami ") + "]"));

        var record = Assert.Single(records);
        Assert.Equal("Miami", record.CityName);
        Assert.Equal("Us", record.SiigoCountryCode);
    }

    [Fact]
    public void ColombiaInTheFileIsRejected() =>
        Assert.Throws<InvalidOperationException>(() => BillingCityDataParser.Parse(Json("[" + Row("Co", "09", "1") + "]")));

    [Fact]
    public void ARepeatedCountryStateAndCityIsRejected() =>
        Assert.Throws<InvalidOperationException>(() =>
            BillingCityDataParser.Parse(Json("[" + Row("Us", "09", "1") + "," + Row("US", "09", "1") + "]")));

    [Fact]
    public void AnEmptyFieldIsRejected() =>
        Assert.Throws<InvalidOperationException>(() =>
            BillingCityDataParser.Parse(Json("[" + Row("Us", "09", "1", " ") + "]")));
}
