using Modules.Geography.Domain;

namespace Modules.Geography.UnitTests;

public sealed class CityTests
{
    [Fact]
    public void CreateWithValidFiveDigitMunicipalityCodeSucceeds()
    {
        var departmentId = DepartmentId.New();

        var city = City.Create(CityId.New(), "05001", "MEDELLÍN", departmentId);

        Assert.Equal("05001", city.DivipolaCode);
        Assert.Equal("MEDELLÍN", city.Name);
        Assert.Equal(departmentId, city.DepartmentId);
    }

    [Fact]
    public void CreateWithValidEightDigitPopulatedCenterCodeSucceeds()
    {
        var departmentId = DepartmentId.New();

        var city = City.Create(
            CityId.New(), "05001000", "MEDELLÍN, DISTRITO ESPECIAL", departmentId);

        Assert.Equal("05001000", city.DivipolaCode);
        Assert.Equal("MEDELLÍN, DISTRITO ESPECIAL", city.Name);
        Assert.Equal(departmentId, city.DepartmentId);
    }

    [Theory]
    [InlineData("5001")]
    [InlineData("050010")]
    [InlineData("0500100")]
    [InlineData("ABCDE")]
    [InlineData("ABCDEFGH")]
    [InlineData("")]
    public void CreateWithInvalidCodeThrows(string code)
    {
        Assert.Throws<GeographyDomainException>(
            () => City.Create(CityId.New(), code, "MEDELLÍN", DepartmentId.New()));
    }

    [Fact]
    public void CreateWithEmptyNameThrows()
    {
        Assert.Throws<GeographyDomainException>(
            () => City.Create(CityId.New(), "05001", "   ", DepartmentId.New()));
    }

    [Fact]
    public void RenameUpdatesTheNameForFutureDivipolaYears()
    {
        var city = City.Create(CityId.New(), "05001", "MEDELLÍN", DepartmentId.New());

        city.Rename("MEDELLÍN RENOMBRADO");

        Assert.Equal("MEDELLÍN RENOMBRADO", city.Name);
    }

    [Fact]
    public void RenameWithEmptyNameThrows()
    {
        var city = City.Create(CityId.New(), "05001", "MEDELLÍN", DepartmentId.New());

        Assert.Throws<GeographyDomainException>(() => city.Rename(""));
    }
}
