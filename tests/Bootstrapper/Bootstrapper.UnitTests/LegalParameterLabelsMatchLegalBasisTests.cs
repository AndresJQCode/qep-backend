using Modules.Hr.Domain;
using Modules.Payroll.Domain.Engine;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Los nombres de los valores legales viven dos veces: <c>LegalBasis</c> (rastro de nómina) y <c>LegalParameterLabels</c> (consola
/// del operador), porque Hr no puede referenciar Payroll. Esta prueba, que ve a los dos módulos, evita que digan cosas distintas.
/// </summary>
public sealed class LegalParameterLabelsMatchLegalBasisTests
{
    [Fact]
    public void NamesMatchLegalBasis()
    {
        foreach (var key in Enum.GetValues<LegalParameterKey>())
        {
            Assert.Equal(LegalBasis.LabelOf(key.ToString()), LegalParameterLabels.Of(key).Name);
        }

        Assert.Equal(LegalBasis.LabelOf("SolidarityFund"), LegalParameterLabels.NameOf(LegalBracketKey.SolidarityFund));
    }
}
