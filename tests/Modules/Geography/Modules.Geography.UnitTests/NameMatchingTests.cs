using Modules.Geography.Infrastructure.Persistence;

namespace Modules.Geography.UnitTests;

public sealed class NameMatchingTests
{
    // La fila del Excel de importacion de clientes trae el nombre tal cual lo tipeo el usuario;
    // DIVIPOLA lo trae siempre con tilde. Sin normalizar, "Bogota" (sin tilde) no encontraba
    // "BOGOTÁ, D.C." aunque la ciudad existiera.
    [Theory]
    [InlineData("BOGOTÁ, D.C.", "bogotá, d.c.")]
    [InlineData("BOGOTÁ, D.C.", "Bogota, D.C.")]
    [InlineData("BOGOTÁ, D.C.", "  bogotá, d.c.  ")]
    [InlineData("MEDELLÍN", "medellin")]
    [InlineData("BOYACÁ", "Boyaca")]
    [InlineData("CAÑASGORDAS", "Canasgordas")]
    public void MatchesRegardlessOfCaseOrAccents(string stored, string typed)
    {
        Assert.Equal(NameMatching.Normalize(stored), NameMatching.Normalize(typed));
    }

    [Theory]
    [InlineData("BOGOTÁ, D.C.", "MEDELLÍN")]
    [InlineData("BOYACÁ", "BOLÍVAR")]
    public void DoesNotMatchDifferentNames(string first, string second)
    {
        Assert.NotEqual(NameMatching.Normalize(first), NameMatching.Normalize(second));
    }
}
