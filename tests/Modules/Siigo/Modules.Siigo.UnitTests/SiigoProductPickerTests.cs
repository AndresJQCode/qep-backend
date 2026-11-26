using Modules.Siigo.Application;
using Modules.Siigo.Application.Products;

namespace Modules.Siigo.UnitTests;

/// <summary>El selector del paso 6 busca en los productos activos de Siigo por código o por nombre, y pagina de a 20 ordenado por código.</summary>
public sealed class SiigoProductPickerTests
{
    private static SiigoProductOption Product(string code, string name) => new($"id-{code}", code, name, true);

    private static readonly IReadOnlyList<SiigoProductOption> Catalog =
    [
        Product("ZETA-1", "Camiseta básica"),
        Product("AB-2", "Pantalón largo"),
        Product("AB-10", "Pantalon corto"),
        Product("cam-7", "Gorra"),
    ];

    [Fact]
    public void WithoutASearchEveryProductComesOrderedByCode()
    {
        var page = SiigoProductPicker.Page(Catalog, search: null, page: 1);

        Assert.Equal(["AB-10", "AB-2", "cam-7", "ZETA-1"], page.Items.Select(item => item.Code));
        Assert.Equal(4, page.Total);
        Assert.Equal(1, page.Page);
        Assert.Equal(SiigoProductPicker.PageSize, page.PageSize);
    }

    [Fact]
    public void ASearchMatchesTheCodeOrTheNameWithoutCaseOrAccents()
    {
        Assert.Equal(["cam-7", "ZETA-1"], SiigoProductPicker.Page(Catalog, "cam", 1).Items.Select(item => item.Code));
        Assert.Equal(["AB-10", "AB-2"], SiigoProductPicker.Page(Catalog, "PANTALON", 1).Items.Select(item => item.Code));
        Assert.Equal(["ZETA-1"], SiigoProductPicker.Page(Catalog, "  básica ", 1).Items.Select(item => item.Code));
    }

    [Fact]
    public void ASearchWithNoMatchIsAnEmptyPageNotAnError()
    {
        var page = SiigoProductPicker.Page(Catalog, "nada", 1);

        Assert.Empty(page.Items);
        Assert.Equal(0, page.Total);
    }

    [Fact]
    public void PagesHoldTwentyAndTheTotalCountsEveryMatch()
    {
        var many = Enumerable.Range(1, 45).Select(index => Product($"P-{index:000}", $"Producto {index}")).ToList();

        var second = SiigoProductPicker.Page(many, null, 2);
        var last = SiigoProductPicker.Page(many, null, 3);
        var beyond = SiigoProductPicker.Page(many, null, 4);

        Assert.Equal(45, second.Total);
        Assert.Equal("P-021", second.Items[0].Code);
        Assert.Equal(20, second.Items.Count);
        Assert.Equal(5, last.Items.Count);
        Assert.Empty(beyond.Items);
    }
}
