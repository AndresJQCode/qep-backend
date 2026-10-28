using Modules.Catalog.Application;

namespace Modules.Catalog.UnitTests;

/// <summary>
/// El <c>code</c> de una fila del listado de productos (decisión del owner, 2026-10-21). Un producto sin
/// opciones se vende como un producto simple aunque guarde variantes inactivas (las que quedan al quitarle
/// todas las opciones), así que la grilla muestra el código de su única variante activa y no «N variantes».
/// </summary>
public sealed class ProductListCodeTests
{
    [Fact]
    public void ASingleVariantShowsItsCode() =>
        Assert.Equal("CAM-1", ProductListCode.For(hasOptions: false, new(1, "CAM-1", 1, "CAM-1")));

    [Fact]
    public void WithoutOptionsTheOnlyActiveVariantShowsItsCodeDespiteInactiveOnes() =>
        Assert.Equal("CAM-M", ProductListCode.For(hasOptions: false, new(3, "CAM-L", 1, "CAM-M")));

    [Fact]
    public void WithOptionsSeveralVariantsShowNoCodeEvenIfOnlyOneIsActive() =>
        Assert.Null(ProductListCode.For(hasOptions: true, new(2, "CAM-M", 1, "CAM-M")));

    // Una sola variante, aunque esté inactiva: su código, como antes.
    [Fact]
    public void ASingleInactiveVariantStillShowsItsCode() =>
        Assert.Equal("CAM-1", ProductListCode.For(hasOptions: false, new(1, "CAM-1", 0, null)));

    // Sin opciones y sin ninguna activa entre varias no hay una que mostrar.
    [Fact]
    public void WithoutOptionsAndNoActiveAmongSeveralShowsNoCode() =>
        Assert.Null(ProductListCode.For(hasOptions: false, new(2, "CAM-L", 0, null)));
}
