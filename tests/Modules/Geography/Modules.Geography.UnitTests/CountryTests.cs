using Modules.Geography.Domain;

namespace Modules.Geography.UnitTests;

public sealed class CountryTests
{
    [Theory]
    [InlineData("co")]
    [InlineData("COL")]
    [InlineData("1A")]
    public void ACodeMustBeTwoUppercaseLetters(string code)
    {
        Assert.Throws<GeographyDomainException>(() => Country.Create(code, "Colombia"));
    }

    [Fact]
    public void ABlankNameIsRejected()
    {
        Assert.Throws<GeographyDomainException>(() => Country.Create("CO", "   "));
    }

    [Fact]
    public void RenameChangesOnlyTheName()
    {
        var country = Country.Create("CO", "Colombia");

        country.Rename("Colombia (renombrado)");

        Assert.Equal("CO", country.Code);
        Assert.Equal("Colombia (renombrado)", country.Name);
    }
}
