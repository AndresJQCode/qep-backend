using Modules.Siigo.Domain;

namespace Modules.Siigo.UnitTests;

/// <summary>Spec 2026-11-18 §5.2: la cuenta de cobro no tiene id en la foto del pedido, así que la clave del vínculo es banco y número
/// normalizados. El separador evita que «AB»+«1» choque con «A»+«B1».</summary>
public sealed class BankAccountKeyTests
{
    [Fact]
    public void TheKeyIsUppercaseWithoutSpacesDashesOrDots() =>
        Assert.Equal("BANCOLOMBIA|000123456", BankAccountKey.Normalize("Bancolombia", "000-123.45 6"));

    [Theory]
    [InlineData(null, "1")]
    [InlineData("Bancolombia", null)]
    [InlineData("  ", "1")]
    [InlineData("Bancolombia", " - . ")]
    public void AMissingPartGivesNoKey(string? bank, string? number) => Assert.Null(BankAccountKey.Normalize(bank, number));

    [Fact]
    public void TheSeparatorKeepsTwoDifferentAccountsApart() =>
        Assert.NotEqual(BankAccountKey.Normalize("AB", "1"), BankAccountKey.Normalize("A", "B1"));
}
