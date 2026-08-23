using Modules.Customers.Application;

namespace Modules.Customers.UnitTests;

/// <summary>
/// La pieza pura y reutilizable de la Fase 4: arma el CUC a partir de sus tres partes. Sin base de
/// datos ni mocks — es exactamente lo que <c>ICucGenerator</c> y <c>ICustomerGeographyLookup</c>
/// no son, a proposito.
/// </summary>
public sealed class CucFormatterTests
{
    // El ejemplo del contrato: prefijo CLI, departamento 08 (Antioquia), consecutivo 1.
    [Fact]
    public void BuildConcatenatesPrefixDepartmentAndPaddedSequenceWithoutSeparators()
    {
        var cuc = CucFormatter.Build("CLI", "08", 1);

        Assert.Equal("CLI08000001", cuc);
    }

    [Fact]
    public void BuildPadsTheSequenceToSixDigits()
    {
        Assert.Equal("CLI08000001", CucFormatter.Build("CLI", "08", 1));
        Assert.Equal("CLI08000042", CucFormatter.Build("CLI", "08", 42));
        Assert.Equal("CLI08123456", CucFormatter.Build("CLI", "08", 123456));
    }

    // Pasados los seis digitos el consecutivo simplemente crece: recortar seria emitir un CUC
    // repetido, y el indice unico lo rechazaria. Mismo criterio que el CucGenerator anterior.
    [Fact]
    public void BuildLetsTheSequenceGrowPastSixDigitsInsteadOfTruncating()
    {
        var cuc = CucFormatter.Build("CLI", "08", 1234567);

        Assert.Equal("CLI081234567", cuc);
    }

    [Fact]
    public void BuildUsesTheClassificationPrefixVerbatim()
    {
        Assert.Equal("MAY08000001", CucFormatter.Build("MAY", "08", 1));
        Assert.Equal("A08000001", CucFormatter.Build("A", "08", 1));
    }

    [Fact]
    public void BuildUsesTheDepartmentCodeVerbatim()
    {
        Assert.Equal("CLI11000001", CucFormatter.Build("CLI", "11", 1));
    }

    // El caso limite de longitud del contrato: prefijo de 20 (ClientClassification.PrefixMaxLength)
    // + departamento de 2 + consecutivo de 6 = 28, que entra en Customer.CucMaxLength (32) sin
    // ajustar esa constante.
    [Fact]
    public void BuildAtTheMaximumPrefixLengthFitsWithinTheCucColumn()
    {
        var maxPrefix = new string('P', 20);

        var cuc = CucFormatter.Build(maxPrefix, "08", 1);

        Assert.Equal(28, cuc.Length);
        Assert.True(cuc.Length <= 32);
        Assert.StartsWith(maxPrefix, cuc, StringComparison.Ordinal);
        Assert.EndsWith("08000001", cuc, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildFormatsTheSequenceInInvariantCulture()
    {
        var originalCulture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture =
                System.Globalization.CultureInfo.GetCultureInfo("ar-SA");

            var cuc = CucFormatter.Build("CLI", "08", 1);

            Assert.Equal("CLI08000001", cuc);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Theory]
    [InlineData("", "08")]
    [InlineData("CLI", "")]
    public void BuildRejectsAnEmptyPrefixOrDepartmentCode(string prefix, string departmentCode)
    {
        Assert.Throws<ArgumentException>(() => CucFormatter.Build(prefix, departmentCode, 1));
    }
}
