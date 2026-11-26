using System.Reflection;
using Modules.Payroll.Domain.Engine;

namespace Modules.Payroll.UnitTests;

public sealed class LegalBasisTests
{
    // Cada valor legal que el motor lee sale en el rastro con su etiqueta y su fundamento: una clave sin texto es un hueco visible.
    [Fact]
    public void EveryKeyHasALabelAndABasis()
    {
        var keys = typeof(LegalValueSet).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.PropertyType == typeof(LegalValue))
            .Select(property => property.Name)
            .ToArray();

        Assert.NotEmpty(keys);
        foreach (var key in keys)
        {
            Assert.False(string.IsNullOrWhiteSpace(LegalBasis.LabelOf(key)), key);
            Assert.False(string.IsNullOrWhiteSpace(LegalBasis.BasisOf(key)), key);
        }
    }

    [Fact]
    public void TheConnectivityStartDateAndTheBracketsAlsoHaveText()
    {
        Assert.Equal("Ley 2121/2021", LegalBasis.BasisOf("ConnectivityAllowanceSince"));
        Assert.Equal("Ley 100/1993 art. 27", LegalBasis.BasisOf("SolidarityFund"));
    }

    [Fact]
    public void AnUnknownKeyThrows()
    {
        Assert.Throws<KeyNotFoundException>(() => LegalBasis.LabelOf("NoSuchKey"));
        Assert.Throws<KeyNotFoundException>(() => LegalBasis.BasisOf("NoSuchKey"));
    }

    [Fact]
    public void TheIntegralSalaryFloorCitesTheNumeralAndItsReform() =>
        Assert.Equal("CST art. 132, num. 2 (mod. Ley 50 de 1990, art. 18)", LegalBasis.BasisOf("IntegralSalaryFloor"));
}
