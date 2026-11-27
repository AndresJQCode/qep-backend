using Modules.Geography.Domain;

namespace Modules.Geography.UnitTests;

public sealed class ForeignCitySynonymTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Operator = Guid.Parse("01900000-0000-7000-8000-0000000000aa");

    private static ForeignCitySynonym NewSynonym(BillingCityId? city = null) =>
        ForeignCitySynonym.Create(
            ForeignCitySynonymId.New(),
            UnresolvedForeignCity.Create(UnresolvedForeignCityId.New(), "US", " Miámy ", Now),
            city ?? BillingCityId.New(),
            Operator,
            Now);

    [Fact]
    public void ASynonymTakesTheKeyAndTextOfItsRow()
    {
        var city = BillingCityId.New();

        var synonym = NewSynonym(city);

        Assert.Equal(("US", "MIAMY", "Miámy", city), (synonym.CountryCode, synonym.NormalizedText, synonym.DisplayText, synonym.BillingCityId));
        Assert.Equal((Operator, Operator, 1L, false), (synonym.CreatedBy, synonym.UpdatedBy, synonym.Version, synonym.IsDeleted));
    }

    [Fact]
    public void ARowWithAnEmptyKeyIsRejected()
    {
        var row = UnresolvedForeignCity.Create(UnresolvedForeignCityId.New(), "US", "́", Now);

        var error = Assert.Throws<GeographyDomainException>(() =>
            ForeignCitySynonym.Create(ForeignCitySynonymId.New(), row, BillingCityId.New(), Operator, Now));

        Assert.Equal("geography.synonym.text_required", error.Code);
    }

    [Fact]
    public void ChangingTheCityReturnsThePreviousOne()
    {
        var first = BillingCityId.New();
        var second = BillingCityId.New();
        var synonym = NewSynonym(first);

        var previous = synonym.ChangeCity(second, Operator, Now.AddMinutes(1));

        Assert.Equal((first, second, 2L), (previous, synonym.BillingCityId, synonym.Version));
    }

    [Fact]
    public void ChangingToTheSameCityIsSameCity()
    {
        var city = BillingCityId.New();
        var synonym = NewSynonym(city);

        var error = Assert.Throws<GeographyDomainException>(() => synonym.ChangeCity(city, Operator, Now));

        Assert.Equal("geography.synonym.same_city", error.Code);
    }

    [Fact]
    public void DeletingTwiceIsDeleted()
    {
        var synonym = NewSynonym();
        synonym.Delete(Operator, Now);

        var delete = Assert.Throws<GeographyDomainException>(() => synonym.Delete(Operator, Now));
        var change = Assert.Throws<GeographyDomainException>(() => synonym.ChangeCity(BillingCityId.New(), Operator, Now));

        Assert.Equal("geography.synonym.deleted", delete.Code);
        Assert.Equal("geography.synonym.deleted", change.Code);
        Assert.True(synonym.IsDeleted);
    }
}
