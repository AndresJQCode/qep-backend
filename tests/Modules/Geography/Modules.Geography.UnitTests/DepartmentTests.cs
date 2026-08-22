using Modules.Geography.Domain;

namespace Modules.Geography.UnitTests;

public sealed class DepartmentTests
{
    [Fact]
    public void CreateWithValidCodeAndNameSucceeds()
    {
        var department = Department.Create(DepartmentId.New(), "05", "ANTIOQUIA");

        Assert.Equal("05", department.DivipolaCode);
        Assert.Equal("ANTIOQUIA", department.Name);
    }

    [Theory]
    [InlineData("5")]
    [InlineData("005")]
    [InlineData("AB")]
    [InlineData("")]
    public void CreateWithInvalidCodeThrows(string code)
    {
        Assert.Throws<GeographyDomainException>(
            () => Department.Create(DepartmentId.New(), code, "ANTIOQUIA"));
    }

    [Fact]
    public void CreateWithEmptyNameThrows()
    {
        Assert.Throws<GeographyDomainException>(
            () => Department.Create(DepartmentId.New(), "05", "   "));
    }

    [Fact]
    public void RenameUpdatesTheNameForFutureDivipolaYears()
    {
        var department = Department.Create(DepartmentId.New(), "05", "ANTIOQUIA");

        department.Rename("ANTIOQUIA RENOMBRADO");

        Assert.Equal("ANTIOQUIA RENOMBRADO", department.Name);
    }

    [Fact]
    public void RenameWithEmptyNameThrows()
    {
        var department = Department.Create(DepartmentId.New(), "05", "ANTIOQUIA");

        Assert.Throws<GeographyDomainException>(() => department.Rename(""));
    }
}
