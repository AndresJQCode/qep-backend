using Modules.Hr.Domain;

namespace Modules.Hr.UnitTests;

/// <summary>El catálogo fijo de cajas de compensación de la sede (spec RRHH fase 1, delta 2026-11-21).</summary>
public sealed class HrCompensationFundsTests
{
    [Fact]
    public void TheCatalogHasTheFortyTwoFundsOfTheUgppList() =>
        Assert.Equal(42, HrCompensationFunds.All.Count);

    [Fact]
    public void NamesAreUniqueAndNitsAreUnique()
    {
        Assert.Equal(42, HrCompensationFunds.Names.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(42, HrCompensationFunds.All.Select(fund => fund.Nit).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void TheThirtyFundsWithoutMeasuresComeBeforeTheTwelveIntervenedOnes()
    {
        Assert.Equal("CAFAMAZ", HrCompensationFunds.Names[0]);
        Assert.Equal("COMFENALCO Antioquia", HrCompensationFunds.Names[30]);
        Assert.Equal("COMFAMILIAR Risaralda", HrCompensationFunds.Names[41]);
    }

    [Theory]
    [InlineData("COMFAMA", true)]
    [InlineData("COMFACHOCÓ", true)]
    [InlineData("comfama", false)]
    [InlineData("Caja inventada", false)]
    [InlineData("", false)]
    public void ContainsMatchesTheExactName(string name, bool expected) =>
        Assert.Equal(expected, HrCompensationFunds.Contains(name));
}
