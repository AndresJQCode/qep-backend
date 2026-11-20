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
        Assert.Equal("distribuciones-nandu-s-a-s", TenantSlugs.Normalize("Distribuciones Ñandú S.A.S."));

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
