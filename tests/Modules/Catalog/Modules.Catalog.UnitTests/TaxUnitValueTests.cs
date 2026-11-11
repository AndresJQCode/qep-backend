using Modules.Catalog.Domain;

namespace Modules.Catalog.UnitTests;

public sealed class TaxUnitValueTests
{
    private const int CurrentYear = 2026;
    private static readonly Guid Operator = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private static TaxUnitValue Unit(int year, decimal value = 49799m, string country = "CO") =>
        TaxUnitValue.Create(country, year, value, Operator, Now, CurrentYear);

    [Theory]
    [InlineData("ES")]
    [InlineData("ZZ")]
    public void CountryWithoutUnitIsRejected(string country)
    {
        var error = Assert.Throws<CatalogDomainException>(() => Unit(2026, country: country));
        Assert.Equal("catalog.tax_unit.country_without_unit", error.Code);
    }

    [Theory]
    [InlineData(1999)]
    [InlineData(CurrentYear + 2)]
    public void YearOutOfRange(int year)
    {
        var error = Assert.Throws<CatalogDomainException>(() => Unit(year));
        Assert.Equal("catalog.tax_unit.year_out_of_range", error.Code);
    }

    [Theory]
    [InlineData(2000)]
    [InlineData(CurrentYear + 1)]
    public void YearsInRangePass(int year) => Assert.Equal(year, Unit(year).Year);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ValueMustBePositive(int value)
    {
        var error = Assert.Throws<CatalogDomainException>(() => Unit(2026, value));
        Assert.Equal("catalog.tax_unit.value_invalid", error.Code);

        var replace = Assert.Throws<CatalogDomainException>(
            () => Unit(2026).Replace(value, Operator, Now));
        Assert.Equal("catalog.tax_unit.value_invalid", replace.Code);
    }

    [Fact]
    public void ReplaceKeepsCountryAndYearAndStampsTheAuthor()
    {
        var unit = Unit(2026, 100m);
        var other = Guid.CreateVersion7();
        var later = Now.AddDays(1);

        unit.Replace(200m, other, later);

        Assert.Equal(200m, unit.Value);
        Assert.Equal(other, unit.UpdatedBy);
        Assert.Equal(later, unit.UpdatedAt);
        Assert.Equal("CO", unit.Country);
        Assert.Equal(2026, unit.Year);
    }

    [Fact]
    public void ValueForUsesTheDocumentYear()
    {
        var values = new[] { Unit(2025, 100m), Unit(2026, 200m) };
        Assert.Equal(200m, TaxUnitValues.ValueFor(values, "CO", 2026));
    }

    [Fact]
    public void ValueForFallsBackToTheLatestEarlierYear()
    {
        var values = new[] { Unit(2024, 100m), Unit(2026, 200m) };
        Assert.Equal(100m, TaxUnitValues.ValueFor(values, "CO", 2025));
        Assert.Equal(200m, TaxUnitValues.ValueFor(values, "CO", 2028));
    }

    [Fact]
    public void ValueForWithOnlyLaterYearsIsNull()
    {
        var values = new[] { Unit(2027, 100m) };
        Assert.Null(TaxUnitValues.ValueFor(values, "CO", 2026));
    }

    [Fact]
    public void DecemberTwentyThirdIsFalse() =>
        Assert.False(TaxUnitValues.NextYearMissing([Unit(2026)], "CO", new DateOnly(2026, 12, 23)));

    [Fact]
    public void DecemberTwentyFourthWithoutNextYearIsTrue() =>
        Assert.True(TaxUnitValues.NextYearMissing([Unit(2026)], "CO", new DateOnly(2026, 12, 24)));

    [Fact]
    public void WithNextYearLoadedIsFalse() =>
        Assert.False(TaxUnitValues.NextYearMissing([Unit(2026), Unit(2027)], "CO", new DateOnly(2026, 12, 24)));

    [Fact]
    public void CountryWithoutUnitIsNeverMissing() =>
        Assert.False(TaxUnitValues.NextYearMissing([], "ES", new DateOnly(2026, 12, 30)));
}
