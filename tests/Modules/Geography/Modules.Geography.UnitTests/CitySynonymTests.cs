using Modules.Geography.Domain;

namespace Modules.Geography.UnitTests;

public sealed class CitySynonymTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static CitySynonym NewSynonym(CityId? cityId = null) =>
        CitySynonym.Create(CitySynonymId.New(), DepartmentId.New(), "Calli", cityId ?? CityId.New(), Guid.NewGuid(), Now);

    [Fact]
    public void CreateNormalizesAndKeepsTheDisplayText()
    {
        var by = Guid.NewGuid();
        var cityId = CityId.New();

        var synonym = CitySynonym.Create(CitySynonymId.New(), DepartmentId.New(), "  Calli ", cityId, by, Now);

        Assert.Equal("CALLI", synonym.NormalizedText);
        Assert.Equal("Calli", synonym.DisplayText);
        Assert.Equal(cityId, synonym.CityId);
        Assert.Equal(by, synonym.CreatedBy);
        Assert.Equal(by, synonym.UpdatedBy);
        Assert.Equal(Now, synonym.CreatedAt);
        Assert.Equal(Now, synonym.UpdatedAt);
        Assert.Equal(1, synonym.Version);
        Assert.False(synonym.IsDeleted);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("")]
    public void CreateWithoutTextIs422TextRequired(string text)
    {
        var ex = Assert.Throws<GeographyDomainException>(() =>
            CitySynonym.Create(CitySynonymId.New(), DepartmentId.New(), text, CityId.New(), Guid.NewGuid(), Now));

        Assert.Equal("geography.synonym.text_required", ex.Code);
    }

    [Fact]
    public void CreateWithMoreThan120CharactersIs422TextTooLong()
    {
        var ex = Assert.Throws<GeographyDomainException>(() =>
            CitySynonym.Create(CitySynonymId.New(), DepartmentId.New(), new string('a', 121), CityId.New(), Guid.NewGuid(), Now));

        Assert.Equal("geography.synonym.text_too_long", ex.Code);
    }

    [Fact]
    public void CreateAcceptsExactly120Characters()
    {
        var synonym = CitySynonym.Create(CitySynonymId.New(), DepartmentId.New(), new string('a', 120), CityId.New(), Guid.NewGuid(), Now);

        Assert.Equal(120, synonym.NormalizedText.Length);
    }

    [Fact]
    public void ChangeCityReturnsThePreviousAndBumpsVersion()
    {
        var previous = CityId.New();
        var next = CityId.New();
        var editor = Guid.NewGuid();
        var later = Now.AddHours(1);
        var synonym = NewSynonym(previous);

        var returned = synonym.ChangeCity(next, editor, later);

        Assert.Equal(previous, returned);
        Assert.Equal(next, synonym.CityId);
        Assert.Equal(editor, synonym.UpdatedBy);
        Assert.Equal(later, synonym.UpdatedAt);
        Assert.Equal(2, synonym.Version);
    }

    [Fact]
    public void ChangeCityToTheSameIs422SameCity()
    {
        var cityId = CityId.New();
        var synonym = NewSynonym(cityId);

        var ex = Assert.Throws<GeographyDomainException>(() => synonym.ChangeCity(cityId, Guid.NewGuid(), Now));

        Assert.Equal("geography.synonym.same_city", ex.Code);
        Assert.Equal(1, synonym.Version);
    }

    [Fact]
    public void DeleteMarksItDeletedAndBumpsVersion()
    {
        var by = Guid.NewGuid();
        var later = Now.AddHours(2);
        var synonym = NewSynonym();

        synonym.Delete(by, later);

        Assert.True(synonym.IsDeleted);
        Assert.Equal(later, synonym.DeletedAt);
        Assert.Equal(by, synonym.DeletedBy);
        Assert.Equal(by, synonym.UpdatedBy);
        Assert.Equal(later, synonym.UpdatedAt);
        Assert.Equal(2, synonym.Version);
    }

    [Fact]
    public void ADeletedSynonymRejectsChangeAndDelete()
    {
        var synonym = NewSynonym();
        synonym.Delete(Guid.NewGuid(), Now);

        var change = Assert.Throws<GeographyDomainException>(() => synonym.ChangeCity(CityId.New(), Guid.NewGuid(), Now));
        var delete = Assert.Throws<GeographyDomainException>(() => synonym.Delete(Guid.NewGuid(), Now));

        Assert.Equal("geography.synonym.deleted", change.Code);
        Assert.Equal("geography.synonym.deleted", delete.Code);
    }
}
