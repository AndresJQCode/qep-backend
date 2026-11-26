using Modules.Hr.Application;

namespace Modules.Hr.UnitTests;

/// <summary>La caja de compensación de una sede: lista fija y sólo para empresas de Colombia (delta 2026-11-21).</summary>
public sealed class HrSiteCompensationFundRuleTests
{
    [Fact]
    public void AColombianCompanyAcceptsACatalogNameOrNone()
    {
        Assert.Null(HrSiteCompensationFund.Check("CO", "COMFAMA"));
        Assert.Null(HrSiteCompensationFund.Check("CO", null));
        Assert.Null(HrSiteCompensationFund.Check("CO", "  "));
    }

    [Theory]
    [InlineData("Caja inventada")]
    [InlineData("comfama")]
    [InlineData(" COMFAMA")]
    public void AColombianCompanyRejectsAnythingOutsideTheCatalog(string fund) =>
        Assert.Equal("Elige una caja de compensación de la lista.", HrSiteCompensationFund.Check("CO", fund));

    [Theory]
    [InlineData("COMFAMA")]
    [InlineData("Caja inventada")]
    public void AForeignCompanyRejectsAnyValue(string fund) =>
        Assert.Equal("La caja de compensación sólo aplica a empresas de Colombia.", HrSiteCompensationFund.Check("US", fund));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void AForeignCompanyAcceptsNone(string? fund) =>
        Assert.Null(HrSiteCompensationFund.Check("US", fund));
}
