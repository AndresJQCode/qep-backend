using FluentValidation;
using Modules.Hr.Application;
using Modules.Hr.Domain;

namespace Modules.Hr.UnitTests;

public sealed class EmployeeCountryTests
{
    private static readonly HrCountryIndex Catalog = new(
    [
        new HrCountryRef("CO", "Colombia"),
        new HrCountryRef("PE", "Perú"),
        new HrCountryRef("VE", "Venezuela"),
        new HrCountryRef("US", "Estados Unidos"),
    ]);

    private static string FailedField(Action action) =>
        Assert.Single(Assert.Throws<ValidationException>(action).Errors).PropertyName;

    [Fact]
    public void NullTakesTheTenantCountry() => Assert.Equal("MX", EmployeeCountry.Effective(null, "MX"));

    [Fact]
    public void WithoutTenantCountryItIsColombia() => Assert.Equal("CO", EmployeeCountry.Effective(null, null));

    [Fact]
    public void TheRequestedCountryWins() => Assert.Equal("VE", EmployeeCountry.Effective("ve", "CO"));

    [Fact]
    public void ABlankCountryIsNoCountry() => Assert.Equal("MX", EmployeeCountry.Effective("  ", " mx "));

    [Fact]
    public void AnUnknownCountryIsRejectedOnCountry() =>
        Assert.Equal("country", FailedField(() => EmployeeCountry.EnsureKnown("DE", Catalog)));

    [Fact]
    public void ACountryOfTheCatalogPasses() => EmployeeCountry.EnsureKnown("PE", Catalog);

    [Fact]
    public void ACcNeedsColombia() =>
        Assert.Equal("country", FailedField(() => EmployeeCountry.Ensure("VE", HrDocumentType.CC, false)));

    [Theory]
    [InlineData(HrDocumentType.CE)]
    [InlineData(HrDocumentType.PEP)]
    [InlineData(HrDocumentType.PPT)]
    [InlineData(HrDocumentType.PA)]
    [InlineData(HrDocumentType.CD)]
    [InlineData(HrDocumentType.SC)]
    public void AForeignDocumentCannotBeColombian(HrDocumentType type)
    {
        Assert.Equal("country", FailedField(() => EmployeeCountry.Ensure("CO", type, false)));
        EmployeeCountry.Ensure("VE", type, false);
    }

    [Fact]
    public void ForeignPensionExemptNeedsAForeignCountry()
    {
        Assert.Equal("foreignPensionExempt", FailedField(() => EmployeeCountry.Ensure("CO", HrDocumentType.CC, true)));
        EmployeeCountry.Ensure("VE", HrDocumentType.PPT, true);
    }
}
