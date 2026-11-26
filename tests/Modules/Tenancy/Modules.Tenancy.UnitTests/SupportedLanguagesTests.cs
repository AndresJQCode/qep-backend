using BuildingBlocks.Domain.Localization;

namespace Modules.Tenancy.UnitTests;

// BuildingBlocks no tiene proyecto de pruebas propio; el catálogo se amarra acá, donde vive su
// primer consumidor (mismo criterio que SnapshotText en Pos).
public sealed class SupportedLanguagesTests
{
    [Fact]
    public void TheCatalogueIsLatinAmericaThenSpain() =>
        Assert.Equal(["es-419", "es-ES"], SupportedLanguages.All);

    [Fact]
    public void TheDefaultIsLatinAmericanSpanish() =>
        Assert.Equal("es-419", SupportedLanguages.Default);

    [Theory]
    [InlineData("es-419", "es-419")]
    [InlineData("ES-419", "es-419")]
    [InlineData(" es-es ", "es-ES")]
    public void TryNormalizeAcceptsTheCatalogueIgnoringCaseAndSpaces(string value, string expected)
    {
        Assert.True(SupportedLanguages.TryNormalize(value, out var language));
        Assert.Equal(expected, language);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("es")]
    [InlineData("es-CO")]
    [InlineData("es_ES")]
    public void TryNormalizeRejectsAnythingElse(string? value) =>
        Assert.False(SupportedLanguages.TryNormalize(value, out _));

    [Theory]
    [InlineData("es-ES", "es-419", "es-ES")]
    [InlineData(null, "es-ES", "es-ES")]
    [InlineData(null, null, "es-419")]
    [InlineData("xx", "es-ES", "es-ES")]
    public void EffectiveIsTheChosenOneThenTheTenantThenTheDefault(string? chosen, string? tenant, string expected) =>
        Assert.Equal(expected, SupportedLanguages.Effective(chosen, tenant));

    [Theory]
    [InlineData(null, "es-419")]
    [InlineData("fr-FR", "es-419")]
    [InlineData("es-es", "es-ES")]
    public void OrDefaultFallsBackToLatinAmerica(string? value, string expected) =>
        Assert.Equal(expected, SupportedLanguages.OrDefault(value));
}
