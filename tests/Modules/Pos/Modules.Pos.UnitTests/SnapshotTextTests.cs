using BuildingBlocks.Domain;

namespace Modules.Pos.UnitTests;

/// <summary>
/// La regla compartida de recorte de una foto de texto (<see cref="SnapshotText.Fit"/>), la que usan
/// <c>QuotationProductSnapshot</c> y <c>PosSaleLine</c> para el nombre visible de una variante. Vive
/// junto a <see cref="VatIncludedLineTests"/> porque BuildingBlocks no tiene proyecto de pruebas
/// propio y Pos es uno de sus dos consumidores. Cada módulo conserva además su prueba de que la línea
/// guarda el nombre recortado (<c>PosSaleTests.ASaleLineStoresTheCutName</c>,
/// <c>QuotationProductSnapshotTests.SendingStoresTheCutName</c>).
/// </summary>
public sealed class SnapshotTextTests
{
    [Fact]
    public void ATextThatFitsIsKeptWhole()
    {
        var exact = new string('a', 200);

        Assert.Equal(exact, SnapshotText.Fit(exact, 200));
        Assert.Equal("Camiseta – M", SnapshotText.Fit("Camiseta – M", 200));
    }

    [Fact]
    public void ALongerTextIsCutToTheMaxLengthWithAnEllipsis()
    {
        var name = new string('p', 200) + " – " + new string('v', 60) + " / Azul";

        var fitted = SnapshotText.Fit(name, 200);

        Assert.Equal(200, fitted.Length);
        Assert.EndsWith("…", fitted, StringComparison.Ordinal);
        Assert.Equal(name[..199], fitted[..199]);
    }

    // El largo es del llamador: la misma regla sirve para otra columna.
    [Fact]
    public void TheMaxLengthIsTheCallers()
    {
        Assert.Equal("abcd…", SnapshotText.Fit("abcdefghij", 5));
        Assert.Equal("abcde", SnapshotText.Fit("abcde", 5));
    }

    // Un emoji ocupa dos char (par sustituto). Si el corte cae en medio, corta uno antes: la mitad
    // suelta no es UTF-16 válido y Npgsql no la puede codificar.
    [Fact]
    public void TheCutNeverSplitsASurrogatePair()
    {
        var name = new string('a', 198) + "😀" + new string('b', 10);

        var fitted = SnapshotText.Fit(name, 200);

        Assert.Equal(new string('a', 198) + "…", fitted);
        Assert.DoesNotContain(fitted, char.IsSurrogate);
    }

    // Los espacios que quedan justo antes del corte no se guardan delante del «…».
    [Fact]
    public void TrailingSpacesBeforeTheEllipsisAreRemoved()
    {
        Assert.Equal("Camiseta…", SnapshotText.Fit("Camiseta    – XL / Azul", 13));
    }

    [Fact]
    public void ANullTextOrAMaxLengthBelowTwoIsAnError()
    {
        Assert.Throws<ArgumentNullException>(() => SnapshotText.Fit(null!, 200));
        Assert.Throws<ArgumentOutOfRangeException>(() => SnapshotText.Fit("abc", 1));
    }
}
