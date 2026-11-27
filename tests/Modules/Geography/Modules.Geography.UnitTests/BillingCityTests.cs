using Modules.Geography.Domain;

namespace Modules.Geography.UnitTests;

public sealed class BillingCityTests
{
    [Fact]
    public void TheCountryIsUppercasedAndTheSiigoCodeKeptAsItCame()
    {
        var city = BillingCity.Create(BillingCityId.New(), "Us", " Estados Unidos ", "09", "Florida ", "1", " Miami ");

        Assert.Equal(("US", "Us"), (city.CountryCode, city.SiigoCountryCode));
        Assert.Equal(
            ("Estados Unidos", "Florida", "Miami", "MIAMI"),
            (city.CountryName, city.StateName, city.CityName, city.NormalizedName));
        Assert.Equal(("09", "1"), (city.StateCode, city.CityCode));
    }

    [Theory]
    [InlineData("USA")]
    [InlineData("U1")]
    [InlineData("")]
    public void ACountryThatIsNotTwoLettersIsRejected(string country) =>
        Assert.Equal(
            "geography.billing_city.country_invalid",
            Assert.Throws<GeographyDomainException>(
                () => BillingCity.Create(BillingCityId.New(), country, "X", "1", "X", "1", "X")).Code);

    [Fact]
    public void AnEmptyFieldIsRejected() =>
        Assert.Equal(
            "geography.billing_city.field_invalid",
            Assert.Throws<GeographyDomainException>(
                () => BillingCity.Create(BillingCityId.New(), "Us", "X", "1", "X", "1", " ")).Code);

    [Fact]
    public void AFieldWiderThanItsColumnIsRejected() =>
        Assert.Equal(
            "geography.billing_city.field_invalid",
            Assert.Throws<GeographyDomainException>(
                () => BillingCity.Create(BillingCityId.New(), "Us", "X", new string('1', 11), "X", "1", "X")).Code);
}
