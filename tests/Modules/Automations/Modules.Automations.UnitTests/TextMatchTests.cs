using Modules.Automations.Domain.Text;

namespace Modules.Automations.UnitTests;

public sealed class TextMatchTests
{
    [Fact]
    public void NormalizeStripsAccentsCaseAndSpaces() => Assert.Equal("servicio tecnico", TextMatch.Normalize("  Servicio   Técnico "));

    [Fact]
    public void NormalizeCollapsesTabsAndNewlines() => Assert.Equal("a b c", TextMatch.Normalize("A\tB\n\n C"));

    [Fact]
    public void SameIgnoresAccentsAndCase() => Assert.True(TextMatch.Same("Mantenimiento", "mantenimiento"));

    [Fact]
    public void SameTreatsNullAsEmpty() => Assert.True(TextMatch.Same(null, "  "));

    [Fact]
    public void IsAnyOfMatchesKeywords() => Assert.True(TextMatch.IsAnyOf("ACEPTO", ["acepto", "sí"]));

    [Fact]
    public void IsAnyOfIgnoresAccentsOnTheCandidate() => Assert.True(TextMatch.IsAnyOf(" si ", ["acepto", "sí"]));

    [Fact]
    public void IsAnyOfIsFalseWhenNothingMatches() => Assert.False(TextMatch.IsAnyOf("no", ["acepto", "sí"]));

    [Fact]
    public void ContainsFindsAFragmentWithoutAccentsOrCase() => Assert.True(TextMatch.Contains("Quiero una COTIZACIÓN hoy", "cotizacion"));

    [Fact]
    public void ContainsIsFalseForAnEmptyFragment() => Assert.False(TextMatch.Contains("hola", "  "));
}
