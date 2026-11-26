using Modules.Tenancy.Application;

namespace Modules.Tenancy.UnitTests;

/// <summary>
/// Spec 2026-11-19 (onboarding por link) §3b: el slug del tenant del distribuidor sale de la razón social. Sin tildes, sólo
/// letras y dígitos ASCII separados por un guion, de 3 a 63 caracteres: las mismas reglas que valida <c>Tenant.Create</c>.
/// </summary>
public sealed class TenantSlugsTests
{
    [Fact]
    public void StripsAccentsAndPunctuation() =>
        Assert.Equal("distribuciones-nandu-cia", TenantSlugs.Normalize("Distribuciones Ñandú & Cía."));

    // Spec 2026-11-19 §3b: la forma societaria del final no entra al slug (sí queda en el nombre del tenant).
    [Theory]
    [InlineData("Distribuciones Andina SAS", "distribuciones-andina")]
    [InlineData("Distribuciones Andina S.A.S", "distribuciones-andina")]
    [InlineData("Distribuciones Andina S.A.S.", "distribuciones-andina")]
    [InlineData("Distribuciones Andina, S.A.S.", "distribuciones-andina")]
    [InlineData("Distribuciones Andina SA", "distribuciones-andina")]
    [InlineData("Distribuciones Andina S.A", "distribuciones-andina")]
    [InlineData("Distribuciones Andina S.A.", "distribuciones-andina")]
    [InlineData("Distribuciones Andina LTDA", "distribuciones-andina")]
    [InlineData("Distribuciones Andina Ltda.", "distribuciones-andina")]
    [InlineData("Distribuciones Andina LTA", "distribuciones-andina")]
    [InlineData("distribuciones andina s.a.s.", "distribuciones-andina")]
    [InlineData("Distribuciones Andina Ltda. S.A.S.", "distribuciones-andina")]
    public void TheCorporateSuffixAtTheEndIsNotPartOfTheSlug(string displayName, string expected) =>
        Assert.Equal(expected, TenantSlugs.Normalize(displayName));

    // Sólo cuenta como sufijo una palabra suelta al final: no un final de palabra, ni algo en el medio, ni el nombre entero.
    [Theory]
    [InlineData("Casas", "casas")]
    [InlineData("Pesa", "pesa")]
    [InlineData("Tasa Bosa", "tasa-bosa")]
    [InlineData("SA Distribuciones", "sa-distribuciones")]
    [InlineData("S.A.S. Andina", "s-a-s-andina")]
    [InlineData("SAS", "sas")]
    [InlineData("Andina S.A.S. Norte", "andina-s-a-s-norte")]
    public void ASuffixLookalikeIsKept(string displayName, string expected) =>
        Assert.Equal(expected, TenantSlugs.Normalize(displayName));

    [Theory]
    [InlineData("A")]
    [InlineData("")]
    [InlineData("  ¡! ")]
    [InlineData("é-")]
    public void ShortNamesFallBackToDistribuidor(string displayName) =>
        Assert.Equal("distribuidor", TenantSlugs.Normalize(displayName));

    [Fact]
    public void LongNamesAreCutAt63()
    {
        // El corte cae justo después de un separador: no puede quedar un guion al final.
        var name = new string('a', 62) + " " + new string('b', 20);

        var slug = TenantSlugs.Normalize(name);

        Assert.Equal(new string('a', 62), slug);
        Assert.True(TenantSlugs.Normalize(new string('x', 100)).Length == TenantSlugs.MaxLength);
    }
}
