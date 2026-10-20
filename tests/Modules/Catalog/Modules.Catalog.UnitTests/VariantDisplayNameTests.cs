using Modules.Catalog.Domain;

namespace Modules.Catalog.UnitTests;

public sealed class VariantDisplayNameTests
{
    [Fact]
    public void DisplayNameDefaultVariantIsProductName()
    {
        Assert.Equal("Camiseta básica", VariantDisplayName.For("Camiseta básica", []));
    }

    [Fact]
    public void DisplayNameJoinsValuesWithSlash()
    {
        Assert.Equal(
            "Camiseta básica – M / Azul",
            VariantDisplayName.For("Camiseta básica", ["M", "Azul"]));
    }
}
